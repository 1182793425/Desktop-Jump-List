using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DesktopJumpList.Infrastructure;
using Microsoft.Win32;

namespace DesktopJumpList.Services;

/// <summary>
/// One installed application discovered in the registry's Uninstall keys. ExePath is the
/// launchable main program (resolved once at scan time); UninstallString is kept only for
/// a future "卸载" action and never launched as the app itself.
/// </summary>
public sealed class AppEntry
{
    public string Name { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ExePath { get; init; } = string.Empty;
    public string InstallLocation { get; init; } = string.Empty;
    public string UninstallString { get; init; } = string.Empty;

    public string Lower { get; init; } = string.Empty;
    public string Initials { get; init; } = string.Empty;
}

/// <summary>
/// Registry-backed index of installed applications (HKLM / HKLM WOW64 / HKCU Uninstall keys).
/// Deliberately independent of the file index: it ignores the folder blacklist and every
/// other file-index rule, so installed apps are searchable no matter which drives are indexed.
/// </summary>
/// <remarks>
/// Incremental update design: a background thread registers RegNotifyChangeKeyValue on each
/// Uninstall key (subtree, LAST_SET + NAME). A notification fires one debounced rebuild; a
/// 30-minute wait timeout acts as a safety-net rescan in case a watcher ever misses a change.
/// A rebuild rescans the whole key set (a few hundred values — cheap), diffs against the
/// snapshot, and only then raises <see cref="Changed"/>, so an unchanged registry costs nothing.
/// </remarks>
public sealed class AppIndex : IDisposable
{
    private const int RescanIntervalMinutes = 30;
    private const int DebounceMs = 1200;

    private readonly object _gate = new();
    private List<AppEntry> _apps = [];
    private HashSet<string> _exePaths = new(StringComparer.OrdinalIgnoreCase);

    private Thread? _watcher;
    private EventWaitHandle? _notifyEvent;
    private EventWaitHandle? _stopEvent;
    private IntPtr[] _watchHandles = [];
    private bool _started;

    /// <summary>Raised on a worker thread after the snapshot actually changed.</summary>
    public event Action? Changed;

    public int Count { get { lock (_gate) return _apps.Count; } }

    /// <summary>Starts the initial scan plus the registry watchers. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _stopEvent = new ManualResetEvent(false);
            _notifyEvent = new ManualResetEvent(false);
        }

        Rebuild();
        _watcher = new Thread(WatchLoop) { IsBackground = true, Name = "AppIndexWatch" };
        _watcher.Start();
    }

    public void Dispose()
    {
        try { _stopEvent?.Set(); } catch { }
        lock (_gate)
        {
            _started = false;
            _watcher = null;
        }
    }

    // ───────────────────────────── Search ─────────────────────────────

    /// <summary>
    /// Same ranking ladder as the file index: exact name, name prefix, name substring,
    /// pinyin-initial prefix / substring, then a publisher match.
    /// </summary>
    public List<AppEntry> Search(string query, int max)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0) return [];

        var needle = query.Trim().ToLowerInvariant();
        var hits = new List<(AppEntry entry, int rank)>();

        lock (_gate)
        {
            foreach (var app in _apps)
            {
                var rank = Rank(app, needle);
                if (rank == int.MaxValue) continue;
                hits.Add((app, rank));
            }
        }

        return hits
            .OrderBy(hit => hit.rank)
            .ThenBy(hit => hit.entry.Name.Length)
            .Take(max)
            .Select(hit => hit.entry)
            .ToList();
    }

    private static int Rank(AppEntry app, string needle)
    {
        if (string.Equals(app.Lower, needle, StringComparison.Ordinal)) return 0;
        if (app.Lower.StartsWith(needle, StringComparison.Ordinal)) return 1;
        if (app.Lower.Contains(needle, StringComparison.Ordinal)) return 2;
        if (app.Initials.Length >= needle.Length && app.Initials.StartsWith(needle, StringComparison.Ordinal)) return 3;
        if (app.Initials.Length > needle.Length && app.Initials.Contains(needle, StringComparison.Ordinal)) return 4;
        if (needle.Length >= 2 && app.Publisher.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)) return 5;
        return int.MaxValue;
    }

    /// <summary>True when this path is the launch target of a registry app (dedup helper).</summary>
    public bool IsKnownAppPath(string path)
    {
        lock (_gate) return _exePaths.Contains(path);
    }

    public List<AppEntry> Snapshot()
    {
        lock (_gate) return _apps.ToList();
    }

    // ───────────────────────────── Scan ─────────────────────────────

    private static readonly string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public void Rebuild()
    {
        var apps = Scan();
        bool changed;
        lock (_gate)
        {
            changed = !SameSnapshot(_apps, apps);
            if (changed)
            {
                _apps = apps;
                _exePaths = new HashSet<string>(
                    apps.Select(a => a.ExePath), StringComparer.OrdinalIgnoreCase);
            }
        }
        if (changed) { try { Changed?.Invoke(); } catch { } }
    }

    private static bool SameSnapshot(List<AppEntry> a, List<AppEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].ExePath, b[i].ExePath, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a[i].Version, b[i].Version, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static List<AppEntry> Scan()
    {
        var byKey = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        // HKLM twice (both registry views — 32-bit installers register under WOW64) and HKCU once.
        foreach (var (hive, view) in new[] {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            RegistryKey? baseKey = null;
            RegistryKey? uninstall = null;
            try
            {
                baseKey = SafeOpenBase(hive, view);
                uninstall = baseKey is null ? null : TryOpen(baseKey, UninstallKeyPath);
                if (uninstall is null) continue;

                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    using var key = TryOpen(uninstall, sub);
                    if (key is null) continue;

                    // Hidden plumbing entries (update bundles, KB components): never launchable.
                    if ((key.GetValue("SystemComponent") as int?) == 1) continue;

                    var name = key.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var install = (key.GetValue("InstallLocation") as string)?.Trim() ?? string.Empty;
                    var uninstallString = (key.GetValue("UninstallString") as string)?.Trim() ?? string.Empty;
                    var exe = ResolveExe(name, install, key.GetValue("DisplayIcon") as string);

                    // Nothing launchable → not an app row (an uninstaller alone is useless here).
                    if (exe.Length == 0) continue;

                    var entry = new AppEntry
                    {
                        Name = name.Trim(),
                        Publisher = (key.GetValue("Publisher") as string)?.Trim() ?? string.Empty,
                        Version = (key.GetValue("DisplayVersion") as string)?.Trim() ?? string.Empty,
                        ExePath = exe,
                        InstallLocation = install,
                        UninstallString = uninstallString,
                        Lower = name.Trim().ToLowerInvariant(),
                        Initials = Pinyin.Initials(name.Trim()),
                    };
                    // WOW64 views surface the same program twice; first hit wins.
                    var id = $"{name.Trim().ToLowerInvariant()}|{exe.ToLowerInvariant()}";
                    if (!byKey.ContainsKey(id)) byKey[id] = entry;
                }
            }
            catch { /* one broken hive must not kill the scan */ }
            finally
            {
                uninstall?.Dispose();
                baseKey?.Dispose();
            }
        }

        return byKey.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static RegistryKey? SafeOpenBase(RegistryHive hive, RegistryView view)
    {
        try { return RegistryKey.OpenBaseKey(hive, view); }
        catch { return null; }
    }

    private static RegistryKey? TryOpen(RegistryKey parent, string path)
    {
        try { return parent.OpenSubKey(path); }
        catch { return null; }
    }

    /// <summary>
    /// Picks the executable a user would want to start: DisplayIcon first (it almost always
    /// points at the main exe), otherwise the top-level exe in InstallLocation that best
    /// matches the product name, falling back to the largest non-uninstaller exe.
    /// </summary>
    private static string ResolveExe(string displayName, string installLocation, string? displayIcon)
    {
        // DisplayIcon is "C:\...\App.exe,0" (quotes optional, icon index optional).
        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            var candidate = displayIcon.Trim().Trim('"');
            var comma = candidate.LastIndexOf(',');
            if (comma > 0 && candidate[(comma + 1)..].Trim().All(char.IsDigit))
                candidate = candidate[..comma].Trim().Trim('"');
            candidate = Environment.ExpandEnvironmentVariables(candidate);
            var candidateStem = Path.GetFileNameWithoutExtension(candidate);
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(candidate)
                // Some vendors point DisplayIcon at their uninstaller — never launchable.
                && !candidateStem.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                && !candidateStem.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        if (installLocation.Length == 0 || !Directory.Exists(installLocation)) return string.Empty;

        string[] topExes;
        try
        {
            topExes = Directory.GetFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly);
        }
        catch { return string.Empty; }

        // Name-shape match: "360安全卫士" → "360safe" etc. Compare letters only.
        static string Letters(string text) =>
            new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var nameLetters = Letters(displayName);

        string? best = null;
        var bestSize = -1L;
        var bestNameMatch = false;
        foreach (var exe in topExes)
        {
            var file = new FileInfo(exe);
            var stem = Path.GetFileNameWithoutExtension(exe);
            // Uninstallers and helpers are never the app itself.
            if (stem.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                || stem.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("setup", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("update", StringComparison.OrdinalIgnoreCase)) continue;

            var exeLetters = Letters(stem);
            var nameMatch = exeLetters.Length > 0 && (
                nameLetters.Contains(exeLetters, StringComparison.OrdinalIgnoreCase)
                || exeLetters.Contains(nameLetters, StringComparison.OrdinalIgnoreCase));

            // A name match always beats a size match; within the same class the bigger exe wins
            // (the main program is almost always the largest file in the folder).
            if (best is not null && (bestNameMatch || !nameMatch) && file.Length <= bestSize) continue;

            best = exe;
            bestSize = file.Length;
            bestNameMatch = nameMatch;
        }

        return best ?? string.Empty;
    }

    // ───────────────────────────── Watch loop ─────────────────────────────

    private void WatchLoop()
    {
        // One watcher per Uninstall key: HKLM (64-bit view), HKLM WOW64, HKCU.
        var bases = new List<RegistryKey>();
        var keys = new List<RegistryKey>();
        try
        {
            foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
            {
                try
                {
                    var bk = RegistryKey.OpenBaseKey(hive, view);
                    var k = bk.OpenSubKey(UninstallKeyPath, writable: false);
                    if (k is not null) { bases.Add(bk); keys.Add(k); }
                    else bk.Dispose();
                }
                catch { }
            }
            try
            {
                var k = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: false);
                if (k is not null) keys.Add(k);
            }
            catch { }

            if (keys.Count == 0) return; // nothing to watch; the 30-min safety net is gone too — acceptable
            _watchHandles = keys.Select(k => k.Handle.DangerousGetHandle()).ToArray();

            while (true)
            {
                WaitHandle? notify = _notifyEvent;
                WaitHandle? stop = _stopEvent;
                if (notify is null || stop is null) break;

                // Re-arm every key before waiting. A failure (key vanished) just means the
                // next iteration retries; the periodic timeout still rescans.
                foreach (var handle in _watchHandles)
                    ArmWatcher(handle, notify.SafeWaitHandle.DangerousGetHandle());

                var index = WaitHandle.WaitAny(new WaitHandle[] { notify, stop }, TimeSpan.FromMinutes(RescanIntervalMinutes));
                if (index == 1) break; // dispose
                if (index == WaitHandle.WaitTimeout)
                {
                    Rebuild(); // safety net: re-read even without a notification
                    continue;
                }

                // Registry changed: wait out the installer's burst of writes, then rescan.
                try { _stopEvent!.WaitOne(DebounceMs); } catch { }
                if (_stopEvent!.WaitOne(0)) break;
                Rebuild();
            }
        }
        catch { /* a dead watcher must never crash the app */ }
        finally
        {
            foreach (var k in keys) k.Dispose();
            foreach (var b in bases) b.Dispose();
        }
    }

    private static void ArmWatcher(IntPtr keyHandle, IntPtr eventHandle)
    {
        try
        {
            // REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET: subkey add/remove and
            // value writes under Uninstall (subtree) all signal an install/uninstall/repair.
            RegNotifyChangeKeyValue(keyHandle, true, 0x01 | 0x04, eventHandle, asynchronous: true);
        }
        catch { }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern long RegNotifyChangeKeyValue(
        IntPtr hKey, bool watchSubtree, uint notifyFilter, IntPtr hEvent, bool asynchronous);
}
