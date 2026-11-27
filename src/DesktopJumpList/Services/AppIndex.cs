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
    public string Compact { get; init; } = string.Empty;
    public string Initials { get; init; } = string.Empty;
    public string WordInitials { get; init; } = string.Empty;
    public string Pinyin { get; init; } = string.Empty;
    public string ExeStem { get; init; } = string.Empty;
    public string PublisherLower { get; init; } = string.Empty;
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
    /// Same ranking ladder as the panel's tiles (see <see cref="TextMatch.RankName"/>): exact
    /// name, prefix, word prefix, substring, compact substring, initials, subsequence — so
    /// "vscode" finds "Microsoft Visual Studio Code" and "vs code" works too. Only then the exe
    /// name and the publisher.
    /// </summary>
    public List<AppEntry> Search(string query, int max)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0) return [];

        var needle = query.Trim().ToLowerInvariant();
        var compact = TextMatch.Compact(needle);
        var hits = new List<(AppEntry entry, int rank, int gap)>();

        lock (_gate)
        {
            foreach (var app in _apps)
            {
                var rank = Rank(app, needle, compact, out var gap);
                if (rank < 0) continue;
                hits.Add((app, rank, gap));
            }
        }

        return hits
            .OrderBy(hit => hit.rank)
            .ThenBy(hit => hit.gap)
            .ThenBy(hit => hit.entry.Name.Length)
            .Take(max)
            .Select(hit => hit.entry)
            .ToList();
    }

    private static int Rank(AppEntry app, string needle, string compact, out int gap)
    {
        var rank = TextMatch.RankName(app.Lower, app.Compact, app.Initials, app.WordInitials,
            app.Pinyin, needle, compact, out gap);
        if (rank >= 0) return rank;

        gap = 0;
        if (compact.Length == 0) return -1;

        // The binary is what people type for tools with long product names: "code" → Code.exe.
        if (app.ExeStem.Length > 0)
        {
            if (app.ExeStem.StartsWith(compact, StringComparison.Ordinal)) return TextMatch.RankBelowName;
            if (app.ExeStem.Contains(compact, StringComparison.Ordinal)) return TextMatch.RankBelowName + 1;
        }

        if (needle.Length >= 2 && app.PublisherLower.Contains(needle, StringComparison.Ordinal))
            return TextMatch.RankBelowName + 2;

        return -1;
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
                    var exe = ResolveExe(name, install, key.GetValue("DisplayIcon") as string, uninstallString);

                    // Nothing launchable → not an app row (an uninstaller alone is useless here).
                    if (exe.Length == 0) continue;

                    var publisher = (key.GetValue("Publisher") as string)?.Trim() ?? string.Empty;
                    var entry = new AppEntry
                    {
                        Name = name.Trim(),
                        Publisher = publisher,
                        Version = (key.GetValue("DisplayVersion") as string)?.Trim() ?? string.Empty,
                        ExePath = exe,
                        InstallLocation = install,
                        UninstallString = uninstallString,
                        Lower = name.Trim().ToLowerInvariant(),
                        Compact = TextMatch.Compact(name),
                        Initials = Pinyin.Initials(name.Trim()),
                        WordInitials = TextMatch.WordInitials(name.Trim()),
                        Pinyin = Pinyin.Full(name.Trim()),
                        ExeStem = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant(),
                        PublisherLower = publisher.ToLowerInvariant(),
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
    /// points at the main exe), otherwise the exe under InstallLocation that best matches the
    /// product name — top level first, then one directory deeper, because plenty of vendors
    /// install into "&lt;InstallLocation&gt;\bin" or "&lt;InstallLocation&gt;\&lt;Product&gt;" and
    /// register no DisplayIcon at all. Without this the app had no launch target and vanished
    /// from the list entirely (no row, so no icon either).
    /// </summary>
    private static string ResolveExe(string displayName, string installLocation, string? displayIcon,
        string uninstallString)
    {
        var icon = ParseIconRef(displayIcon);

        // DisplayIcon that ends in .exe is the program the vendor itself starts.
        if (icon is { } fromIcon)
        {
            if (fromIcon.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(fromIcon.Path) && !IsHelperExe(fromIcon.Path))
                return fromIcon.Path;
        }

        foreach (var dir in CandidateFolders(installLocation, icon?.Path, uninstallString))
        {
            var found = FindMainExe(dir, displayName, icon?.Path);
            if (found.Length > 0) return found;
        }

        return string.Empty;
    }

    /// <summary>"C:\...\App.exe,0" / quoted / %ENVVAR% / a bare .ico or .dll. Paths are expanded.</summary>
    private static (string Path, int Index)? ParseIconRef(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;

        var text = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        if (text.StartsWith("@", StringComparison.Ordinal)) return null; // indirect string resource

        var index = 0;
        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out var parsed))
        {
            text = text[..comma];
            index = parsed;
        }

        text = text.Trim().Trim('"').Trim();
        return text.Length == 0 ? null : (text, index);
    }

    /// <summary>
    /// Folders that may hold the program, most trustworthy first. Some installers only fill one
    /// of them: InstallLocation, the folder DisplayIcon lives in, or the uninstaller's folder.
    /// </summary>
    private static IEnumerable<string> CandidateFolders(string installLocation, string? iconPath,
        string? uninstallString)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (installLocation.Length > 0)
        {
            var location = installLocation.Trim().Trim('"').Trim();
            if (seen.Add(location) && Directory.Exists(location)) yield return location;
        }

        if (iconPath is { Length: > 0 })
        {
            var dir = SafeDirectoryOf(iconPath);
            if (dir is not null && seen.Add(dir) && Directory.Exists(dir)) yield return dir;
        }

        if (!string.IsNullOrWhiteSpace(uninstallString))
        {
            var quoted = uninstallString.Trim();
            var exe = quoted.StartsWith("\"", StringComparison.Ordinal)
                ? quoted.Trim('"').Split('"')[0]
                : quoted.Split(' ')[0];
            var dir = SafeDirectoryOf(Environment.ExpandEnvironmentVariables(exe));
            if (dir is not null && seen.Add(dir) && Directory.Exists(dir)) yield return dir;
        }
    }

    private static string? SafeDirectoryOf(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }

    /// <summary>Uninstallers, updaters and crash handlers are never the app itself.</summary>
    private static bool IsHelperExe(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
               || stem.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
               || stem.Contains("crashreport", StringComparison.OrdinalIgnoreCase)
               || stem.Contains("crashhandler", StringComparison.OrdinalIgnoreCase)
               || stem.Equals("setup", StringComparison.OrdinalIgnoreCase)
               || stem.Equals("update", StringComparison.OrdinalIgnoreCase)
               || stem.Equals("install", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Best exe in <paramref name="folder"/>, looking at the top level and one level of
    /// subdirectories. Ranking: the exe whose name matches the product name wins, then the one
    /// named like DisplayIcon, then the largest file (the main program is rarely a small stub).
    /// </summary>
    private static string FindMainExe(string folder, string displayName, string? iconPath)
    {
        static string Letters(string text) =>
            new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        var nameLetters = Letters(displayName);
        var iconStem = iconPath is null ? string.Empty : Path.GetFileNameWithoutExtension(iconPath);

        string? best = null;
        var bestScore = int.MinValue;
        var bestSize = -1L;

        foreach (var exe in EnumerateExes(folder, depth: 1))
        {
            var stem = Path.GetFileNameWithoutExtension(exe);
            if (IsHelperExe(exe)) continue;

            long size;
            try { size = new FileInfo(exe).Length; } catch { continue; }

            var exeLetters = Letters(stem);
            var score = 0;
            if (iconStem.Length > 0 && stem.Equals(iconStem, StringComparison.OrdinalIgnoreCase)) score += 4;
            if (exeLetters.Length > 0 && (nameLetters.Contains(exeLetters, StringComparison.OrdinalIgnoreCase)
                                          || exeLetters.Contains(nameLetters, StringComparison.OrdinalIgnoreCase)))
                score += 2;
            // Top-level programs beat the same-named tool buried in a subfolder.
            if (string.Equals(Path.GetDirectoryName(exe), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                score += 1;

            if (score > bestScore || (score == bestScore && size > bestSize))
            {
                best = exe;
                bestScore = score;
                bestSize = size;
            }
        }

        return best ?? string.Empty;
    }

    /// <summary>Exes directly in <paramref name="folder"/>, plus one level of subfolders (bounded).</summary>
    private static IEnumerable<string> EnumerateExes(string folder, int depth)
    {
        string[] files;
        try { files = Directory.GetFiles(folder, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var file in files) yield return file;
        if (depth <= 0) yield break;

        string[] subfolders;
        try { subfolders = Directory.GetDirectories(folder); }
        catch { yield break; }

        var visited = 0;
        foreach (var sub in subfolders)
        {
            if (++visited > 40) break; // a wrongly registered root must not turn into a disk walk
            string[] nested;
            try { nested = Directory.GetFiles(sub, "*.exe", SearchOption.TopDirectoryOnly); }
            catch { continue; }
            foreach (var file in nested) yield return file;
        }
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
