using System.Collections.Concurrent;
using System.IO;
using DesktopJumpList.Infrastructure;

namespace DesktopJumpList.Services;

/// <summary>
/// Keeps an existing index honest without ever touching the disk while the user types.
/// <para>
/// Two change sources feed one delta pipeline. The USN journal is preferred: it survives restarts
/// (the stored anchor replays everything that happened while Jump was closed) and it reports
/// renames and deletions exactly. When the process is not allowed to read the journal — the
/// normal case for an unelevated user — per-root directory watchers take over and a silent
/// reconcile walk covers whatever happened while the app was gone.
/// </para>
/// <para>
/// Everything runs on one low-priority background thread: it polls slowly while the user is
/// active, catches up when the machine is idle, and defers full rebuilds until then.
/// </para>
/// </summary>
public sealed class FileIndexSync : IDisposable
{
    private const int IdleSecondsForRebuild = 45;
    // An index inherited from an earlier run is known to have a hole (the app was closed), so its
    // reconcile should not wait as long as one triggered while the app was running.
    private const int IdleSecondsForStartupReconcile = 5;
    private const int ActivePollMs = 5000;
    private const int IdlePollMs = 1200;

    private readonly FileIndex _index;
    private readonly Func<IndexRules> _rules;
    private readonly Func<bool> _enabled;

    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentDictionary<string, PathChange> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _watcherGate = new();

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _rebuildRequested;
    private volatile bool _reconcileRequested;
    private SourceKind _source = SourceKind.None;

    /// <summary>Used to tell "index built earlier" from "index built by this session".</summary>
    private readonly DateTime _startedAt = DateTime.Now;

    public FileIndexSync(FileIndex index, Func<IndexRules> rules, Func<bool> enabled)
    {
        _index = index;
        _rules = rules;
        _enabled = enabled;
    }

    private enum SourceKind { None, Usn, Watchers }

    public DateTime LastSyncAt { get; private set; } = DateTime.MinValue;

    /// <summary>"USN 日志" / "目录监听" / "未启动".</summary>
    public string SourceLabel => _source switch
    {
        SourceKind.Usn => "USN 日志",
        SourceKind.Watchers => "目录监听",
        _ => "未启动"
    };

    public bool IsLive => _source != SourceKind.None && !_stop;

    /// <summary>Queued watcher paths, for diagnostics.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Last exception seen by the loop, for diagnostics.</summary>
    public string? LastError { get; private set; }

    /// <summary>Last watcher batch summary, for diagnostics.</summary>
    public string LastBatch { get; private set; } = "-";

    /// <summary>Starts watching. Does nothing until an index exists — rebuilding stays manual.</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _stop = false;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Jump.IndexSync",
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _wake.Set();
        DisposeWatchers();
    }

    /// <summary>Settings changed: rebuild the watchers and re-check whether the index is stale.</summary>
    public void Nudge() => _wake.Set();

    /// <summary>
    /// The filter rules changed, so the stored rows were produced by an older rule set.
    /// Rebuilding is deferred until the machine is idle.
    /// </summary>
    public void RequestRebuild()
    {
        _rebuildRequested = true;
        _wake.Set();
    }

    /// <summary>A watcher buffer overflowed or a journal wrapped: only a re-walk can fix that.</summary>
    private void RequestReconcile()
    {
        _reconcileRequested = true;
        _wake.Set();
    }

    // ───────────────────────────── Loop ─────────────────────────────

    private void Run()
    {
        UsnApi.EnterBackgroundMode();
        try
        {
            while (!_stop)
            {
                var interval = ActivePollMs;
                try
                {
                    interval = Tick();
                }
                catch (Exception ex)
                {
                    LastError = ex.GetType().Name + ": " + ex.Message;
                    try { if (System.Windows.Application.Current is DesktopJumpList.App app) app.Log(ex); }
                    catch { }
                }

                if (_stop) break;
                _wake.WaitOne(interval);
            }
        }
        finally
        {
            DisposeWatchers();
            UsnApi.LeaveBackgroundMode();
        }
    }

    private int Tick()
    {
        if (!_enabled())
        {
            DisposeWatchers();
            _source = SourceKind.None;
            return 8000;
        }

        var idle = IdleDetector.SecondsSinceInput();
        var rebuildPending = false;

        if ((_rebuildRequested || _reconcileRequested) && !_index.IsBuilding)
        {
            // Silent rebuilds wait for the user to step away; when there is nothing to serve
            // yet there is no reason to wait at all. A rebuild requested because the rules
            // changed can afford to wait longer than a startup reconcile of a stale index.
            var startupGap = !_rebuildRequested && _index.LastBuilt < _startedAt;
            var threshold = _index.Count == 0
                ? 0
                : startupGap ? IdleSecondsForStartupReconcile : IdleSecondsForRebuild;
            if (idle >= threshold)
            {
                FullRebuild();
                return 2000;
            }
            // Waiting for idle must not freeze the live updates: a pending rebuild is exactly the
            // moment the index is known to be behind, so keep applying deltas in the meantime.
            rebuildPending = true;
        }

        if (!_index.HasIndex)
        {
            // Startup policy: with roots configured but no usable index there is nothing to
            // judge incrementally (no anchor, no rows), so build once automatically instead of
            // waiting for a manual gesture. Roots already chosen by the onboarding wizard or
            // settings drive this; an empty root list still stays out of the way.
            if (!_index.IsBuilding && !_rebuildRequested && !_reconcileRequested
                && _rules().Roots.Count > 0)
            {
                _rebuildRequested = true;
                return 1000;
            }
            DisposeWatchers();
            _source = SourceKind.None;
            return 8000;
        }

        if (_index.IsBuilding) return 2000;

        if (_source == SourceKind.None) ChooseSource();
        if (_source == SourceKind.Usn) PollJournal();
        else DrainWatchers();

        SaveIfNeeded();
        if (rebuildPending) return 3000;
        return idle >= IdleSecondsForRebuild ? IdlePollMs : ActivePollMs;
    }

    private void ChooseSource()
    {
        var rules = _rules();
        var volumes = VolumesOf(rules.Roots);
        if (volumes.Count > 0 && CanConsumeJournal(volumes))
        {
            _source = SourceKind.Usn;
            DisposeWatchers();
            return;
        }

        _source = SourceKind.Watchers;
        BuildWatchers(rules);

        // Watchers only see the future. An index from an earlier run has a gap that the journal
        // cannot fill without privileges, so mark it for one silent reconcile.
        if (_index.LastBuilt < _startedAt) RequestReconcile();
    }

    /// <summary>
    /// Whether the journal can really be consumed, not just queried. An unelevated process is
    /// allowed to query (journal id, first/next usn) but the record read is denied — and that
    /// denial is reported as *zero records without an error* for some positions. So the only
    /// trustworthy proof is a read that actually returns a record; anything less falls back to
    /// the directory watchers, which do work without privileges.
    /// </summary>
    private bool CanConsumeJournal(List<string> volumes)
    {
        foreach (var volume in volumes)
        {
            var anchor = _index.GetAnchor(volume);
            if (anchor is null)
            {
                // No anchor means the delta stream has no start position: only a rebuild creates one.
                RequestReconcile();
                return false;
            }

            using var handle = UsnApi.OpenJournal(volume, out _);
            if (handle.IsInvalid) return false;
            if (!UsnApi.TryQueryJournal(handle, out var journal, out _)) return false;

            // Journal restarted or the anchor fell off the front: the stream has a hole.
            if (journal.UsnJournalID != anchor.JournalId || anchor.NextUsn < journal.FirstUsn)
            {
                RequestReconcile();
                return false;
            }

            // An empty journal cannot prove read access; the watcher is always safe.
            if (journal.FirstUsn >= journal.NextUsn) return false;

            var probe = UsnApi.ReadJournal(handle, journal.FirstUsn, journal.UsnJournalID, 1,
                out _, out var error);
            if (error != 0 || probe.Count == 0) return false;
        }
        return true;
    }

    // ───────────────────────────── USN source ─────────────────────────────

    private void PollJournal()
    {
        var rules = _rules();
        foreach (var volume in VolumesOf(rules.Roots))
        {
            var anchor = _index.GetAnchor(volume);
            if (anchor is null) { RequestReconcile(); return; }

            using var handle = UsnApi.OpenJournal(volume, out _);
            if (handle.IsInvalid) { _source = SourceKind.None; return; }
            if (!UsnApi.TryQueryJournal(handle, out var journal, out _)) { _source = SourceKind.None; return; }

            // Journal id changed or the anchor fell off the front of the journal: the delta
            // stream has a hole, so only a rebuild restores consistency.
            if (journal.UsnJournalID != anchor.JournalId || anchor.NextUsn < journal.FirstUsn)
            {
                RequestReconcile();
                return;
            }

            if (anchor.NextUsn >= journal.NextUsn) continue;

            var position = anchor.NextUsn;
            var batches = 0;
            while (position < journal.NextUsn && batches++ < 8 && !_stop)
            {
                var records = UsnApi.ReadJournal(handle, position, anchor.JournalId, 20_000, out var next, out var error);
                if (error != 0)
                {
                    // Lost the right to read (or a transient failure): drop to the watchers right
                    // away instead of waiting for the next tick, so the gap stays small.
                    _source = SourceKind.None;
                    ChooseSource();
                    return;
                }
                if (next <= position) break;

                _index.ApplyUsn(records, rules);
                position = next;
            }

            _index.SetAnchor(volume, new VolumeAnchor { JournalId = anchor.JournalId, NextUsn = position });
            LastSyncAt = DateTime.Now;
        }
    }

    private static List<string> VolumesOf(IEnumerable<string> roots)
    {
        var volumes = new List<string>();
        foreach (var root in roots)
        {
            var volume = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(volume) || volume.Length < 3) continue;
            if (!volumes.Contains(volume, StringComparer.OrdinalIgnoreCase)) volumes.Add(volume);
        }
        return volumes;
    }

    // ───────────────────────────── Watcher source ─────────────────────────────

    private void BuildWatchers(IndexRules rules)
    {
        DisposeWatchers();
        lock (_watcherGate)
        {
            foreach (var root in rules.Roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        // 64 KB of buffer width: the default 8 KB overflows while an archive is
                        // being extracted, and an overflow costs a full reconcile.
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                       | NotifyFilters.LastWrite | NotifyFilters.Size
                                       | NotifyFilters.Attributes,
                        Filter = "*"
                    };
                    watcher.Created += (_, e) => Enqueue(e.FullPath, deleted: false);
                    watcher.Changed += (_, e) => Enqueue(e.FullPath, deleted: false);
                    watcher.Renamed += (_, e) =>
                    {
                        Enqueue(e.OldFullPath, deleted: true);
                        Enqueue(e.FullPath, deleted: false);
                    };
                    watcher.Deleted += (_, e) => Enqueue(e.FullPath, deleted: true);
                    watcher.Error += (_, _) => RequestReconcile();
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch { /* a root we cannot watch simply stays stale */ }
            }
        }
    }

    private void Enqueue(string path, bool deleted)
    {
        if (string.IsNullOrEmpty(path)) return;

        // Early rejection keeps a whole-drive watcher usable: Windows, Program Files and temp
        // churn all report events, and none of them may ever reach the index.
        var rules = _rules();
        if (rules.IsExcluded(path)) return;
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name.StartsWith('.')) return;
        var extension = Path.GetExtension(path);
        if (extension.Length > 0 && rules.Extensions.Count > 0
            && !rules.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return;

        _pending[path] = new PathChange(path, deleted);
        _wake.Set();
    }

    private void DrainWatchers()
    {
        if (_pending.IsEmpty) return;

        var rules = _rules();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var applied = 0;
        var seen = 0;

        while (!_pending.IsEmpty && stopwatch.ElapsedMilliseconds < 60)
        {
            var batch = new List<PathChange>(512);
            foreach (var key in _pending.Keys)
            {
                if (batch.Count >= 512) break;
                if (_pending.TryRemove(key, out var change)) batch.Add(change);
            }
            if (batch.Count == 0) break;

            applied += _index.ApplyPathChanges(batch, rules);
            seen += batch.Count;
        }

        if (seen == 0) return;
        LastSyncAt = DateTime.Now;
        LastBatch = $"{seen} paths -> {applied} changes";
    }

    private void DisposeWatchers()
    {
        lock (_watcherGate)
        {
            foreach (var watcher in _watchers)
            {
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
                catch { }
            }
            _watchers.Clear();
        }
    }

    // ───────────────────────────── Shared ─────────────────────────────

    private void FullRebuild()
    {
        if (_index.IsBuilding) return;
        _rebuildRequested = false;
        _reconcileRequested = false;
        _source = SourceKind.None;
        DisposeWatchers();
        _index.Start(_rules());
        LastSyncAt = DateTime.Now;
    }

    private DateTime _lastSave = DateTime.MinValue;

    private void SaveIfNeeded()
    {
        if (!_index.IsDirty) return;
        // Writing the whole index on every tick would cost more than the deltas themselves; the
        // anchor is replay-safe, so a longer window is fine. A download or a build can change
        // files continuously — without this, the whole index would be rewritten every few seconds
        // for nothing.
        var elapsed = DateTime.Now - _lastSave;
        if (elapsed < TimeSpan.FromSeconds(30) && _index.DirtyBatches < 40) return;
        _index.Save();
        _lastSave = DateTime.Now;
    }

    public void Dispose()
    {
        Stop();
        // Let the loop leave WaitOne before the handle could be collected; the AutoResetEvent
        // itself is intentionally not disposed — a thread still parked in WaitOne would throw.
        try { _thread?.Join(TimeSpan.FromSeconds(2)); } catch { }
        _thread = null;
    }
}
