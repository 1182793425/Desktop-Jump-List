using System.IO;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

/// <summary>One file or folder found under the indexed roots.</summary>
public sealed record FileEntry(
    string Path,
    string Name,
    string Folder,
    string Lower,
    string Initials,
    DateTime WriteTime,
    bool IsDirectory);

/// <summary>
/// A small in-memory index of the user's folders. Built once on a background thread, refreshed
/// when it goes stale, then searched with plain string work — scanning the disk on every
/// keystroke would be the one thing guaranteed to make typing feel slow.
/// </summary>
public sealed class FileIndex
{
    // Noise folders that never hold files people search for. "bin" is deliberately NOT here:
    // toolchains put their executables in bin (D:\jdk-17\bin\java.exe), which is exactly what
    // "/s java.exe" must find.
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components", ".git", ".svn", ".vs", ".idea", ".vscode",
        "obj", "target", "dist", "build", "__pycache__",
        "AppData", "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "$RECYCLE.BIN", "System Volume Information", "OneDriveTemp"
    };

    private readonly object _gate = new();
    private List<FileEntry> _entries = new();
    private CancellationTokenSource? _cancellation;

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public bool IsBuilding { get; private set; }

    /// <summary>True once the post-walk icon warm-up finished (preview/screenshot helper).</summary>
    public bool IconsWarm { get; private set; }

    public DateTime LastBuilt { get; private set; } = DateTime.MinValue;

    // No background auto-refresh: the index is rebuilt only when the user asks for it via
    // the "重建索引并搜索" row in /s, or changes the folders in advanced settings.

    /// <summary>Starts (or restarts) the background walk. Returns immediately.</summary>
    public void Start(IReadOnlyList<string> roots, int limit, Action? onDone = null)
        => Start(roots, limit, null, null, null, onDone);

    /// <summary>Overload with per-folder depth / entry caps (0 = unlimited).</summary>
    public void Start(IReadOnlyList<string> roots, int limit,
        IReadOnlyDictionary<string, RootLimitSetting>? perRoot, Action? onDone = null)
        => Start(roots, limit, perRoot, null, null, onDone);

    /// <summary>
    /// Full overload. <paramref name="extensionAllowList"/> is the .pathactive-style filter:
    /// only files whose extension is in the list are indexed (folders and the merged apps are
    /// never filtered); null or empty means no filtering. <paramref name="excludedFolders"/>
    /// is the blacklist: no folder under those paths is walked or indexed.
    /// </summary>
    public void Start(IReadOnlyList<string> roots, int limit,
        IReadOnlyDictionary<string, RootLimitSetting>? perRoot,
        IReadOnlyCollection<string>? extensionAllowList, Action? onDone = null)
        => Start(roots, limit, perRoot, extensionAllowList, null, onDone);

    public void Start(IReadOnlyList<string> roots, int limit,
        IReadOnlyDictionary<string, RootLimitSetting>? perRoot,
        IReadOnlyCollection<string>? extensionAllowList,
        IReadOnlyCollection<string>? excludedFolders, Action? onDone = null)
    {
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var token = cancellation.Token;
        IsBuilding = true;
        IconsWarm = false;

        var extensionFilter = extensionAllowList is { Count: > 0 }
            ? new HashSet<string>(extensionAllowList, StringComparer.OrdinalIgnoreCase)
            : null;
        var excludePrefixes = NormalizeExcludes(excludedFolders);

        Task.Run(() =>
        {
            var found = new List<FileEntry>(4096);
            // limit <= 0 (the default) means no cap: entries are ~0.4 KB of path strings, so
            // even a few hundred thousand stay in the tens of MB.
            var cap = limit <= 0 ? int.MaxValue : Math.Max(500, limit);
            try { Walk(roots, cap, perRoot, extensionFilter, excludePrefixes, found, token); }
            catch { /* cancelled or unreadable root */ }

            // The Start Menu shortcuts (classic software) come from the walk when its folders
            // are among the roots; packaged apps (便签, 录音机…) come from this merge instead.
            var appIds = token.IsCancellationRequested
                ? new List<string>()
                : MergeStoreApps(found);

            var cancelled = token.IsCancellationRequested;
            if (!cancelled)
            {
                lock (_gate)
                {
                    _entries = found;
                    LastBuilt = DateTime.Now;
                }
            }

            // Only the newest run owns the flag. A superseded walk must not clear it for
            // the replacement that is still running, or the index would never refresh.
            if (ReferenceEquals(_cancellation, cancellation)) IsBuilding = false;

            if (!cancelled)
            {
                // No icon work here by design: icons render lazily per visible row
                // (QueueDeferredIconLoad), so indexing stays pure path text.
                IconsWarm = true;
                onDone?.Invoke();
            }
        }, token);
    }

    /// <summary>Rough resident size of the index (path strings + per-entry overhead).</summary>
    public long MemoryBytes
    {
        get
        {
            lock (_gate)
            {
                long bytes = 0;
                foreach (var e in _entries)
                    bytes += (e.Path.Length + e.Name.Length + e.Folder.Length + e.Lower.Length + e.Initials.Length) * 2 + 152;
                return bytes;
            }
        }
    }

    /// <summary>Expands %VARS%, trims trailing separators and drops empties.</summary>
    private static List<string> NormalizeExcludes(IReadOnlyCollection<string>? excludedFolders)
    {
        var result = new List<string>();
        if (excludedFolders is null) return result;
        foreach (var raw in excludedFolders)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var expanded = Environment.ExpandEnvironmentVariables(raw.Trim()).TrimEnd('\\');
            if (expanded.Length > 2) result.Add(expanded); // never allow "C:\" or a bare drive
        }
        return result;
    }

    /// <summary>True when the path is one of the excluded folders or lives under one.</summary>
    private static bool IsExcluded(string path, List<string> excludePrefixes)
    {
        foreach (var prefix in excludePrefixes)
        {
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Length > prefix.Length
                && path[prefix.Length] == '\\'
                && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void Walk(IReadOnlyList<string> roots, int limit,
        IReadOnlyDictionary<string, RootLimitSetting>? perRoot,
        HashSet<string>? extensionFilter, List<string> excludePrefixes,
        List<FileEntry> into, CancellationToken token)
    {
        // Roots are walked one by one so each can honour its own depth / entry caps.
        foreach (var root in roots)
        {
            if (token.IsCancellationRequested || into.Count >= limit) break;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            if (IsExcluded(root.TrimEnd('\\'), excludePrefixes)) continue;

            var cfg = perRoot is not null && perRoot.TryGetValue(root, out var found)
                ? found
                : null;
            var depthCap = cfg is null || cfg.MaxDepth <= 0 ? int.MaxValue : cfg.MaxDepth;
            var entryCap = cfg is null || cfg.MaxEntries <= 0 ? int.MaxValue : cfg.MaxEntries;

            WalkOne(root, depthCap, entryCap, limit, extensionFilter, excludePrefixes, into, token);
        }
    }

    private static void WalkOne(string root, int depthCap, int entryCap, int globalLimit,
        HashSet<string>? extensionFilter, List<string> excludePrefixes,
        List<FileEntry> into, CancellationToken token)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var counted = 0;

        while (pending.Count > 0 && counted < entryCap && into.Count < globalLimit)
        {
            token.ThrowIfCancellationRequested();
            var (folder, depth) = pending.Pop();

            IEnumerator<string> children;
            try { children = Directory.EnumerateFileSystemEntries(folder).GetEnumerator(); }
            catch { continue; }

            using (children)
            {
                while (true)
                {
                    if (counted >= entryCap || into.Count >= globalLimit) break;

                    string path;
                    try { if (!children.MoveNext()) break; path = children.Current; }
                    catch { break; }

                    token.ThrowIfCancellationRequested();

                    var name = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;

                    bool isDirectory;
                    try { isDirectory = Directory.Exists(path); }
                    catch { continue; }

                    // .pathactive-style allow-list: files must match, folders always pass.
                    if (!isDirectory && extensionFilter is { Count: > 0 })
                    {
                        var extension = Path.GetExtension(path);
                        if (extension.Length == 0 || !extensionFilter.Contains(extension)) continue;
                    }

                    into.Add(new FileEntry(
                        path, name, folder, name.ToLowerInvariant(),
                        Pinyin.Initials(name),
                        TryWriteTime(path),
                        isDirectory));
                    counted++;

                    if (isDirectory && depth < depthCap && !SkipFolders.Contains(name)
                        && !IsExcluded(path, excludePrefixes))
                        pending.Push((path, depth + 1));
                }
            }
        }
    }

    /// <summary>
    /// A handful of entries for "/s" with no keyword yet: newest touched first, so the
    /// command shows "what did I work on recently" instead of the raw walk order.
    /// </summary>
    public List<FileEntry> Take(int max)
    {
        if (max <= 0) return new List<FileEntry>();
        List<FileEntry> snapshot;
        lock (_gate) snapshot = _entries;
        return snapshot
            .OrderByDescending(e => e.WriteTime)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Ranking ladder, most to least specific: exact name match, name prefix, name substring,
    /// pinyin-initial prefix ("sjg" → 设计稿), pinyin-initial substring, subsequence (3+ chars),
    /// and finally a folder-path match ("jdk" finds D:\jdk-17\bin\java.exe).
    /// </summary>
    public List<FileEntry> Search(string query, int max)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0) return new List<FileEntry>();

        var needle = query.Trim().ToLowerInvariant();
        var compact = needle.Replace(" ", string.Empty);
        var hits = new List<(FileEntry entry, int rank)>();

        List<FileEntry> snapshot;
        lock (_gate) snapshot = _entries;

        foreach (var entry in snapshot)
        {
            var rank = Rank(entry, needle, compact);
            if (rank == int.MaxValue) continue;
            hits.Add((entry, rank));
        }

        // Within one rank the file you touched most recently wins; name length only breaks ties.
        return hits
            .OrderBy(h => h.rank)
            .ThenByDescending(h => h.entry.WriteTime)
            .ThenBy(h => h.entry.Name.Length)
            .Take(max)
            .Select(h => h.entry)
            .ToList();
    }

    private static int Rank(FileEntry entry, string needle, string compact)
    {
        // An exact file-name hit is always the first line ("/s java.exe" must not get lost
        // behind fuzzy matches). int.MaxValue marks "no match".
        if (string.Equals(entry.Lower, needle, StringComparison.Ordinal)) return 0;
        if (entry.Lower.StartsWith(needle, StringComparison.Ordinal)) return 1;
        if (entry.Lower.Contains(needle, StringComparison.Ordinal)) return 2;
        if (entry.Initials.Length >= needle.Length && entry.Initials.StartsWith(needle, StringComparison.Ordinal)) return 3;
        if (entry.Initials.Length > needle.Length && entry.Initials.Contains(needle, StringComparison.Ordinal)) return 4;

        // "vscode" style abbreviations still work, but only from three characters up so that a
        // single letter does not drag the whole disk into the list.
        if (compact.Length >= 3 && IsSubsequence(entry.Lower, compact)) return 5;
        if (compact.Length >= 3 && IsSubsequence(entry.Initials, compact)) return 5;

        // Path match: "/s jdk" should find everything inside a jdk folder, ranked last so
        // name hits always stay on top.
        if (entry.Folder.Contains(needle, StringComparison.OrdinalIgnoreCase)) return 6;
        return int.MaxValue;
    }

    private static DateTime TryWriteTime(string path)
    {
        try { return Directory.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    private const string FileEntryAppsPrefix = "shell:AppsFolder\\";

    /// <summary>
    /// Launchable apps join the index as entries: the folder walk covers classic .lnk software
    /// through the Start Menu, this merge covers 便签 / 录音机 style Windows apps that have no
    /// .lnk at all. Returns the merged AUMIDs so the caller can warm their icons on this
    /// worker thread.
    /// </summary>
    private static List<string> MergeStoreApps(List<FileEntry> into)
    {
        var ids = new List<string>();
        try
        {
            var apps = AppsFolderService.Enumerate();
            var seen = new HashSet<string>(
                into.Select(e => e.Lower), StringComparer.OrdinalIgnoreCase);

            foreach (var app in apps)
            {
                var lower = app.Name.ToLowerInvariant();
                if (lower.Length == 0 || !seen.Add(lower)) continue; // same-name .lnk already walked
                into.Add(new FileEntry(
                    FileEntryAppsPrefix + app.AumId,
                    app.Name,
                    "已安装应用",
                    lower,
                    Pinyin.Initials(app.Name),
                    DateTime.MinValue,
                    IsDirectory: false));
                ids.Add(app.AumId);
            }
        }
        catch { /* Shell unavailable — file results still work */ }
        return ids;
    }

    /// <summary>True for the app rows merged by <see cref="MergeStoreApps"/>.</summary>
    public static bool IsAppEntry(FileEntry entry) =>
        entry.Path.StartsWith(FileEntryAppsPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The AUMID part of an app row's shell:AppsFolder path.</summary>
    public static string AppAumId(FileEntry entry) => entry.Path[FileEntryAppsPrefix.Length..];

    private static bool IsSubsequence(string text, string query)
    {
        var from = 0;
        var first = true;
        foreach (var ch in query)
        {
            if (ch == ' ') continue;
            var found = text.IndexOf(ch, from);
            if (first)
            {
                while (found > 0 && char.IsLetterOrDigit(text[found - 1])) found = text.IndexOf(ch, found + 1);
            }
            if (found < 0) return false;
            from = found + 1;
            first = false;
        }
        return true;
    }

    /// <summary>
    /// Desktop, Documents, Downloads, Pictures — plus both Start Menu folders, so the
    /// installed software (.lnk) is searchable too.
    /// </summary>
    public static List<string> DefaultRoots()
    {
        var roots = new List<string>();
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.UserProfile
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (folder == Environment.SpecialFolder.UserProfile) continue; // too broad on its own
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !roots.Contains(path)) roots.Add(path);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        if (Directory.Exists(downloads) && !roots.Contains(downloads)) roots.Add(downloads);

        foreach (var menu in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         @"Microsoft\Windows\Start Menu\Programs"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         @"Microsoft\Windows\Start Menu\Programs")
                 })
        {
            if (Directory.Exists(menu) && !roots.Contains(menu)) roots.Add(menu);
        }
        return roots;
    }
}
