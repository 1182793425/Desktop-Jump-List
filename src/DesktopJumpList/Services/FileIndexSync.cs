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

        if ((_rebuildRequested || _reconcileRequested) && !_index.IsBuilding)
        {
            // Silent rebuilds wait for the user to step away; when there is nothing to serve
            // yet there is no reason to wait at all.
            var threshold = _index.Count == 0 ? 0 : IdleSecondsForRebuild;
            if (idle >= threshold)
            {
                FullRebuild();
                return 2000;
            }
            return 3000;
        }

        if (!_index.HasIndex)
        {
            // Nothing usable built yet: stay completely out of the way until the user asks.
            DisposeWatchers();
            _source = SourceKind.None;
            return 8000;
        }

        if (_index.IsBuilding) return 2000;

        if (_source == SourceKind.None) ChooseSource();
        if (_source == SourceKind.Usn) PollJournal();
        else DrainWatchers();

        SaveIfNeeded();
        return idle >= IdleSecondsForRebuild ? IdlePollMs : ActivePollMs;
    }

    private void ChooseSource()
    {
        var rules = _rules();
        var volumes = VolumesOf(rules.Roots);
        if (volumes.Count > 0)
        {
            var usable = true;
            foreach (var volume in volumes)
            {
                if (_index.GetAnchor(volume) is null) { usable = false; break; }
                using var handle = UsnApi.OpenJournal(volume, out _);
                if (handle.IsInvalid) { usable = false; break; }
                if (!UsnApi.TryQueryJournal(handle, out var journal, out _)) { usable = false; break; }
                // Prove a read from the stored position is actually permitted before committing:
                // an unelevated process passes the query but is denied the records.
                UsnApi.ReadJournal(handle, journal.NextUsn, journal.UsnJournalID, 1, out _, out var error);
                if (error != 0) { usable = false; break; }
            }

            if (usable)
            {
                _source = SourceKind.Usn;
                DisposeWatchers();
                return;
            }
        }

        _source = SourceKind.Watchers;
        BuildWatchers(rules);

        // Watchers only see the future: if the stored index predates this run by a while, the
        // gap is covered by one silent reconcile once the machine goes idle.
        if (DateTime.Now - _index.LastBuilt > TimeSpan.FromMinutes(30)) RequestReconcile();
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
                    // Lost the right to read (or a transient failure): fall back to watchers
                    // instead of rebuilding in a loop.
                    _source = SourceKind.None;
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
        // Writing the whole index on every tick would cost more than the deltas themselves;
        // the anchor is replay-safe, so a short window is fine.
        if (DateTime.Now - _lastSave < TimeSpan.FromSeconds(10)) return;
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
