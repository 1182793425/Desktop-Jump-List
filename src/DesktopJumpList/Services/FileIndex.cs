using System.IO;
using System.Text;
using System.Text.Json;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

/// <summary>
/// One searchable row: a file, a folder, or a packaged app. <see cref="Id"/> is the NTFS file
/// reference number and is the real primary key — the path is derived from the parent chain, so
/// renaming a folder updates every descendant without re-walking the disk.
/// </summary>
public sealed class FileEntry
{
    /// <summary>NTFS file ID (0 for shell app rows, which are merged after the walk).</summary>
    public ulong Id { get; init; }

    public ulong ParentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }

    /// <summary>Last write time; for live updates this is the moment of the change.</summary>
    public DateTime WriteTime { get; set; }

    public string Path { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string Lower { get; set; } = string.Empty;
    public string Initials { get; set; } = string.Empty;

    /// <summary>
    /// Full pinyin of the name, compact and lower-case ("shejigao"). For an ASCII name this is the
    /// same string as <see cref="Lower"/>, so English names cost nothing extra.
    /// </summary>
    public string Pinyin { get; set; } = string.Empty;
}

/// <summary>Everything that shapes what the index may contain, snapshotted for one operation.</summary>
public sealed class IndexRules
{
    public IndexRules(IReadOnlyList<string> roots, IReadOnlyDictionary<string, RootLimitSetting> perRoot,
        IReadOnlyCollection<string> extensions, IReadOnlyCollection<string> excludes, int limit,
        bool discoverDriveRoots)
    {
        Roots = roots.Select(NormalizePath).Where(path => path.Length > 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        PerRoot = perRoot;
        Extensions = extensions
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .Select(extension => extension.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ExcludePrefixes = excludes
            .Select(NormalizePath)
            .Where(entry => entry.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Limit = limit;
        DiscoverDriveRoots = discoverDriveRoots;
        Key = BuildKey();
    }

    public IReadOnlyList<string> Roots { get; }
    public IReadOnlyDictionary<string, RootLimitSetting> PerRoot { get; }
    public IReadOnlyList<string> Extensions { get; }
    public IReadOnlyList<string> ExcludePrefixes { get; }
    public int Limit { get; }
    public bool DiscoverDriveRoots { get; }

    /// <summary>
    /// Changes whenever the *shape* of the index would change. A mismatch with the persisted key
    /// means the stored rows were filtered by an older rule set and must be rebuilt.
    /// </summary>
    public string Key { get; }

    // Bump when the filtering semantics change, so old indexes are dropped rather than trusted.
    private const int RulesVersion = 3;

    // Windows attribute bits used by the built-in exclusions (System.IO.FileAttributes has the
    // same values, but keeping them local avoids casting noise at every call site).
    private const uint AttributeHidden = 0x00000002;
    private const uint AttributeSystem = 0x00000004;

    private string BuildKey()
    {
        var parts = new List<string> { "v" + RulesVersion, "limit=" + Limit, "drives=" + (DiscoverDriveRoots ? 1 : 0) };
        parts.AddRange(Roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        parts.Add("--ext--");
        parts.AddRange(Extensions.OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase));
        parts.Add("--no--");
        parts.AddRange(ExcludePrefixes.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        parts.Add("--caps--");
        foreach (var (root, cap) in PerRoot.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            parts.Add($"{NormalizePath(root)}={cap.MaxDepth}/{cap.MaxEntries}");

        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', parts));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes), 0, 12);
    }

    public static string NormalizePath(string path)
    {
        var normalized = Environment.ExpandEnvironmentVariables(path.Trim()).Replace('/', '\\');
        // A drive root keeps its separator: trimming "C:\" down to "C:" would make it a
        // *relative* path, and Path.Combine("C:", "Users") is "C:Users".
        return normalized.Length > 3 ? normalized.TrimEnd('\\') : normalized;
    }

    /// <summary>True when <paramref name="path"/> is the prefix itself or lives beneath it.</summary>
    private static bool IsSameOrUnder(string path, string prefix)
    {
        if (prefix.EndsWith('\\'))
            return path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        if (path.Length == prefix.Length) return path.Equals(prefix, StringComparison.OrdinalIgnoreCase);
        return path.Length > prefix.Length
               && path[prefix.Length] == '\\'
               && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsUnderRoot(string path)
    {
        foreach (var root in Roots)
            if (IsSameOrUnder(path, root)) return true;
        return false;
    }

    public bool IsExcluded(string path)
    {
        foreach (var entry in ExcludePrefixes)
        {
            // A manual entry may be a folder path, a bare file/folder name, or a wildcard
            // ("*.bak", "~$*"), so users are not limited to "exclude this one directory".
            if (entry.IndexOf('*') >= 0 || entry.IndexOf('?') >= 0)
            {
                if (MatchesWildcard(path, entry)) return true;
                continue;
            }

            if (entry.IndexOf('\\') < 0 && entry.IndexOf(':') < 0)
            {
                if (string.Equals(Path.GetFileName(path), entry, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }

            if (IsSameOrUnder(path, entry)) return true;
        }
        return false;
    }

    /// <summary>Case-insensitive glob over the whole path: '*' spans separators, '?' is one char.</summary>
    private static bool MatchesWildcard(string path, string pattern)
    {
        int p = 0, s = 0, star = -1, resume = 0;
        while (s < path.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(path[s])))
            {
                p++;
                s++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = s;
            }
            else if (star >= 0)
            {
                p = star + 1;
                s = ++resume;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>
    /// The whole "write it only if it belongs" decision. Directories always pass the extension
    /// list; files must match it when it is non-empty. Dot-names (.git) and Office owner files
    /// (~$report.docx) are dropped, and so is anything the shell marks hidden or system —
    /// that is where the recycle bin's $I/$R metadata and other OS bookkeeping live.
    /// </summary>
    public bool Accepts(string path, string name, bool isDirectory, uint attributes = 0)
    {
        if (name.Length == 0 || name[0] == '.') return false;
        if (name.Length > 1 && name[0] == '~' && name[1] == '$') return false;
        if ((attributes & (AttributeHidden | AttributeSystem)) != 0) return false;
        if (!IsUnderRoot(path) || IsExcluded(path)) return false;
        if (isDirectory) return true;
        if (Extensions.Count == 0) return true;
        var extension = Path.GetExtension(path);
        return extension.Length > 0 && Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Persisted alongside the rows: rule key, root seeds and per-volume journal anchors.</summary>
public sealed class IndexMeta
{
    public string RuleKey { get; set; } = string.Empty;
    public string BuiltAt { get; set; } = string.Empty;
    public bool Complete { get; set; }

    /// <summary>root path → its own file id, so a child's path can be resolved from its chain.</summary>
    public Dictionary<string, ulong> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>volume root ("C:\") → journal anchor after the last successfully applied batch.</summary>
    public Dictionary<string, VolumeAnchor> Volumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class VolumeAnchor
{
    public ulong JournalId { get; set; }
    public long NextUsn { get; set; }
}

/// <summary>
/// The in-memory index plus its on-disk mirror. Queries only ever touch this object — nothing
/// rescans the disk while typing. Updates arrive as deltas (USN journal when the process may read
/// it, directory watchers otherwise) and are applied here under a single lock.
/// </summary>
public sealed class FileIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, FileEntry> _byId = new();
    private readonly Dictionary<string, FileEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, List<ulong>> _childIds = new();
    private readonly Dictionary<ulong, string> _rootSeeds = new();

    private List<FileEntry> _searchList = new();
    private List<(string Extension, int Count)> _extensionStats = new();
    private bool _searchListDirty = true;

    private CancellationTokenSource? _cancellation;
    private readonly string? _directory;
    private bool _dirty;

    public FileIndex(string? directory = null) => _directory = directory;

    public IndexMeta Meta { get; private set; } = new();

    public int Count
    {
        get { lock (_gate) return _byId.Count; }
    }

    public bool IsBuilding { get; private set; }

    /// <summary>True once the post-walk icon warm-up finished (preview/screenshot helper).</summary>
    public bool IconsWarm { get; private set; }

    public DateTime LastBuilt { get; private set; } = DateTime.MinValue;

    /// <summary>Raised after any change to the contents, from whichever thread made it.</summary>
    public event Action? Changed;

    public bool HasIndex
    {
        get { lock (_gate) return _byId.Count > 0 && Meta.Complete; }
    }

    public bool IsDirty
    {
        get { lock (_gate) return _dirty; }
    }

    public long MemoryBytes
    {
        get
        {
            lock (_gate)
            {
                long bytes = 0;
                foreach (var entry in _byId.Values)
                    bytes += (entry.Path.Length + entry.Name.Length + entry.Folder.Length
                              + entry.Lower.Length + entry.Initials.Length + entry.Pinyin.Length) * 2 + 180;
                return bytes;
            }
        }
    }

    // ───────────────────────────── Queries ─────────────────────────────

    private List<FileEntry> SnapshotLocked()
    {
        if (_searchListDirty)
        {
            _searchList = new List<FileEntry>(_byId.Count);
            _searchList.AddRange(_byId.Values);
            _searchListDirty = false;
        }
        return _searchList;
    }

    /// <summary>A handful of entries for "/s" with no keyword yet: newest touched first.</summary>
    public List<FileEntry> Take(int max, int skip = 0)
    {
        if (max <= 0) return new List<FileEntry>();
        List<FileEntry> snapshot;
        lock (_gate) snapshot = SnapshotLocked();
        return snapshot
            .OrderByDescending(entry => entry.WriteTime)
            .Skip(Math.Max(0, skip))
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Ranking ladder, most to least specific: exact name match, name prefix, name substring,
    /// pinyin-initial prefix ("sjg" → 设计稿), pinyin-initial substring, subsequence (3+ chars),
    /// and finally a folder-path match ("jdk" finds D:\jdk-17\bin\java.exe).
    /// </summary>
    public List<FileEntry> Search(string query, int max, int skip = 0)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0) return new List<FileEntry>();

        var needle = query.Trim().ToLowerInvariant();
        var compact = needle.Replace(" ", string.Empty);
        // Space-separated words are separate requirements, not one long string: "/s file 2026 11
        // .csv" has to match a path like ...\Cache\File\2026-11\明细.csv, where no single
        // substring contains all of it.
        var tokens = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<(FileEntry entry, int rank, int folderHits)>();

        List<FileEntry> snapshot;
        lock (_gate) snapshot = SnapshotLocked();

        foreach (var entry in snapshot)
        {
            if (tokens.Length <= 1)
            {
                var single = Rank(entry, needle, compact);
                if (single == int.MaxValue) continue;
                hits.Add((entry, single, 0));
                continue;
            }

            var rank = RankAllTokens(entry, tokens, out var folderHits);
            if (rank == int.MaxValue) continue;
            hits.Add((entry, rank, folderHits));
        }

        return hits
            // Words that hit the *directory* first: typing "file 2026 11 .csv" describes a location,
            // so a file inside …\File\2026-11\ must beat one that merely has "11" in its name.
            .OrderByDescending(hit => hit.folderHits)
            .ThenBy(hit => hit.rank)
            .ThenByDescending(hit => hit.entry.WriteTime)
            .ThenBy(hit => hit.entry.Name.Length)
            .Skip(Math.Max(0, skip))
            .Take(max)
            .Select(hit => hit.entry)
            .ToList();
    }

    /// <summary>
    /// Every token must hit somewhere (name, pinyin, or the folder path); ranks add up and the
    /// number of tokens that matched the folder comes back for the sort.
    /// </summary>
    private static int RankAllTokens(FileEntry entry, string[] tokens, out int folderHits)
    {
        var total = 0;
        folderHits = 0;
        foreach (var token in tokens)
        {
            var rank = Rank(entry, token, token);
            if (rank == int.MaxValue) return int.MaxValue;
            total += rank;
            if (rank >= FolderRank) folderHits++;
        }
        return total;
    }

    private static int Rank(FileEntry entry, string needle, string compact)
    {
        if (string.Equals(entry.Lower, needle, StringComparison.Ordinal)) return 0;
        if (entry.Lower.StartsWith(needle, StringComparison.Ordinal)) return 1;
        if (entry.Lower.Contains(needle, StringComparison.Ordinal)) return 2;
        if (entry.Initials.Length >= needle.Length && entry.Initials.StartsWith(needle, StringComparison.Ordinal)) return 3;
        if (entry.Initials.Length > needle.Length && entry.Initials.Contains(needle, StringComparison.Ordinal)) return 4;

        // Full pinyin: "shejigao" → 设计稿. Only for letter queries, and only when the name really
        // has Han characters (Pinyin equals Lower for ASCII names, where the rules above already
        // covered the same ground).
        if (IsAsciiWord(needle) && entry.Pinyin.Length > 0
            && !string.Equals(entry.Pinyin, entry.Lower, StringComparison.Ordinal))
        {
            if (entry.Pinyin.StartsWith(needle, StringComparison.Ordinal)) return 5;
            if (needle.Length >= 3 && entry.Pinyin.Contains(needle, StringComparison.Ordinal)) return 6;
        }

        if (compact.Length >= 3 && IsSubsequence(entry.Lower, compact)) return 7;
        if (compact.Length >= 3 && IsSubsequence(entry.Initials, compact)) return 7;
        if (entry.Folder.Contains(needle, StringComparison.OrdinalIgnoreCase)) return FolderRank;
        return int.MaxValue;
    }

    /// <summary>Rank of "only the folder path matched" — also marks a token as a folder hit.</summary>
    private const int FolderRank = 8;

    private static bool IsAsciiWord(string text)
    {
        if (text.Length == 0) return false;
        foreach (var ch in text)
            if (ch is < 'a' or > 'z') return false;
        return true;
    }

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

    public List<(string Extension, int Count)> GetExtensionStats()
    {
        lock (_gate) return _extensionStats.ToList();
    }

    private void RebuildStatsLocked()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _byId.Values)
        {
            if (entry.IsDirectory || IsAppEntry(entry)) continue;
            var extension = Path.GetExtension(entry.Path);
            if (extension.Length == 0) continue;
            counts[extension] = counts.GetValueOrDefault(extension) + 1;
        }
        _extensionStats = counts
            .OrderByDescending(pair => pair.Value)
            .Select(pair => (pair.Key, pair.Value))
            .ToList();
    }

    // ───────────────────────────── Full build ─────────────────────────────

    /// <summary>Starts (or restarts) the background walk. Returns immediately.</summary>
    public void Start(IReadOnlyList<string> roots, int limit, Action? onDone = null)
        => Start(roots, limit, null, null, null, onDone);

    public void Start(IReadOnlyList<string> roots, int limit,
        IReadOnlyDictionary<string, RootLimitSetting>? perRoot, Action? onDone = null)
        => Start(roots, limit, perRoot, null, null, onDone);

    public void Start(IReadOnlyList<string> roots, int limit,
        IReadOnlyDictionary<string, RootLimitSetting>? perRoot,
        IReadOnlyCollection<string>? extensionAllowList, Action? onDone = null)
        => Start(roots, limit, perRoot, extensionAllowList, null, onDone);

    public void Start(IReadOnlyList<string> roots, int limit,
        IReadOnlyDictionary<string, RootLimitSetting>? perRoot,
        IReadOnlyCollection<string>? extensionAllowList,
        IReadOnlyCollection<string>? excludedFolders, Action? onDone = null,
        bool discoverDriveRoots = true)
    {
        var rules = new IndexRules(roots, perRoot ?? new Dictionary<string, RootLimitSetting>(),
            extensionAllowList ?? Array.Empty<string>(), excludedFolders ?? Array.Empty<string>(),
            limit, discoverDriveRoots);
        Start(rules, onDone);
    }

    /// <summary>
    /// Full rebuild. Takes the journal anchor *before* enumerating and catches up afterwards, so
    /// changes made during the walk are never lost.
    /// </summary>
    public void Start(IndexRules rules, Action? onDone = null)
    {
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var token = cancellation.Token;
        IsBuilding = true;
        IconsWarm = false;

        Task.Run(() =>
        {
            UsnApi.EnterBackgroundMode();
            var built = Build(rules, token, out var meta, out var cancelled);
            if (!cancelled)
            {
                lock (_gate) ReplaceAllLocked(built, rules, meta);

                // Catch-up: replay everything that happened while we were enumerating. The
                // anchors were taken *before* the walk, so this closes the gap exactly.
                foreach (var (volume, anchor) in meta.Volumes)
                {
                    if (token.IsCancellationRequested) break;
                    using var handle = UsnApi.OpenJournal(volume, out _);
                    if (handle.IsInvalid) continue;
                    if (!UsnApi.TryQueryJournal(handle, out var journal, out _) || journal.UsnJournalID != anchor.JournalId)
                        continue;
                    if (anchor.NextUsn < journal.FirstUsn) continue; // wrapped; next sync rebuilds

                    var position = anchor.NextUsn;
                    while (position < journal.NextUsn && !token.IsCancellationRequested)
                    {
                        var records = UsnApi.ReadJournal(handle, position, anchor.JournalId, 20_000, out var next, out var error);
                        if (error != 0 || next <= position) break;
                        ApplyJournal(records, rules);
                        position = next;
                    }
                    SetAnchor(volume, new VolumeAnchor { JournalId = anchor.JournalId, NextUsn = position });
                }

                lock (_gate) LastBuilt = DateTime.Now;
                Save();
            }

            if (ReferenceEquals(_cancellation, cancellation)) IsBuilding = false;

            if (!cancelled)
            {
                IconsWarm = true;
                Changed?.Invoke();
                onDone?.Invoke();
            }

            UsnApi.LeaveBackgroundMode();
        }, token);
    }

    private List<FileEntry> Build(IndexRules rules, CancellationToken token, out IndexMeta meta, out bool cancelled)
    {
        var found = new List<FileEntry>(4096);
        var seeds = new Dictionary<ulong, string>();
        meta = new IndexMeta();

        // Anchor first: everything the walk misses because it happened mid-flight is replayed
        // from these positions once enumeration is done.
        var anchors = new Dictionary<string, VolumeAnchor>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in VolumesOf(rules.Roots))
        {
            using var handle = UsnApi.OpenJournal(volume, out _);
            if (handle.IsInvalid) continue;
            if (UsnApi.TryQueryJournal(handle, out var journal, out _))
                anchors[volume] = new VolumeAnchor { JournalId = journal.UsnJournalID, NextUsn = journal.NextUsn };
        }

        var cap = rules.Limit <= 0 ? int.MaxValue : Math.Max(500, rules.Limit);
        try
        {
            foreach (var root in rules.Roots)
            {
                token.ThrowIfCancellationRequested();
                if (found.Count >= cap) break;
                if (!Directory.Exists(root)) continue;
                if (rules.IsExcluded(root)) continue;

                rules.PerRoot.TryGetValue(root, out var cfg);
                var depthCap = cfg is null || cfg.MaxDepth <= 0 ? int.MaxValue : cfg.MaxDepth;
                var entryCap = cfg is null || cfg.MaxEntries <= 0 ? int.MaxValue : cfg.MaxEntries;
                WalkRoot(root, rules, depthCap, entryCap, cap, found, seeds, token);
            }

            if (rules.DiscoverDriveRoots && !token.IsCancellationRequested)
                WalkDriveRoots(rules, cap, found, seeds, token);
        }
        catch (OperationCanceledException) { }

        cancelled = token.IsCancellationRequested;
        if (cancelled) return found;

        MergeStoreApps(found);

        meta.RuleKey = rules.Key;
        meta.BuiltAt = DateTime.Now.ToString("O");
        meta.Complete = true;
        meta.Roots = seeds.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        meta.Volumes = anchors;

        return found;
    }

    private static IEnumerable<string> VolumesOf(IEnumerable<string> roots)
    {
        var volumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var volume = Path.GetPathRoot(root);
            if (!string.IsNullOrEmpty(volume) && volume.Length >= 3) volumes.Add(volume);
        }
        return volumes;
    }

    private void WalkRoot(string root, IndexRules rules, int depthCap, int entryCap, int globalLimit,
        List<FileEntry> into, Dictionary<ulong, string> seeds, CancellationToken token)
    {
        // A root that is itself a junction must be validated before anything under it is added.
        var realRoot = UsnApi.TryGetFinalPath(root);
        if (realRoot is not null && !string.Equals(realRoot, root, StringComparison.OrdinalIgnoreCase)
            && rules.IsExcluded(realRoot)) return;

        using var rootHandle = UsnApi.OpenDirectory(root);
        if (rootHandle.IsInvalid) return;
        if (!UsnApi.TryGetIdentity(rootHandle, out var rootId, out _, out _)) return;
        seeds[rootId] = root;

        var pending = new Stack<(string Path, ulong Id, int Depth)>();
        pending.Push((root, rootId, 0));
        var counted = 0;

        while (pending.Count > 0 && counted < entryCap && into.Count < globalLimit)
        {
            token.ThrowIfCancellationRequested();
            var (folder, folderId, depth) = pending.Pop();

            List<UsnApi.DirChild> children;
            using (var handle = UsnApi.OpenDirectory(folder))
            {
                if (handle.IsInvalid) continue;
                // Second filter pass on the *real* path: a directory reached through a junction
                // (C:\Users\All Users → C:\ProgramData) has an innocuous logical path but an
                // excluded target, and only this check closes that hole.
                var realFolder = UsnApi.TryGetFinalPath(handle);
                if (realFolder is not null
                    && !string.Equals(realFolder, folder, StringComparison.OrdinalIgnoreCase)
                    && rules.IsExcluded(realFolder))
                    continue;
                children = UsnApi.ListChildren(handle, int.MaxValue, out _);
            }

            foreach (var child in children)
            {
                token.ThrowIfCancellationRequested();
                if (counted >= entryCap || into.Count >= globalLimit) break;

                var path = Path.Combine(folder, child.Name);
                if (child.IsDirectory && child.IsReparsePoint) continue; // never index through a junction
                if (rules.IsExcluded(path)) continue;
                if (!rules.Accepts(path, child.Name, child.IsDirectory, child.Attributes)) continue;

                var entry = MakeEntry(child.Id, folderId, path, child.Name, child.IsDirectory,
                    FileTime(child.WriteTicks));
                into.Add(entry);
                counted++;

                if (child.IsDirectory && depth < depthCap) pending.Push((path, child.Id, depth + 1));
            }
        }
    }

    /// <summary>
    /// Also surface the immediate children of fixed drives so an unconfigured top-level folder
    /// such as D:\code3 is discoverable. Never descends further.
    /// </summary>
    private void WalkDriveRoots(IndexRules rules, int globalLimit, List<FileEntry> into,
        Dictionary<ulong, string> seeds, CancellationToken token)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var path = drive.RootDirectory.FullName;
                if (rules.Roots.Any(root => string.Equals(Path.GetPathRoot(root), path, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (rules.IsExcluded(path)) continue;
                WalkRoot(path, rules, depthCap: 0, entryCap: int.MaxValue, globalLimit, into, seeds, token);
            }
            catch { /* an unavailable drive is skipped */ }
        }
    }

    private static FileEntry MakeEntry(ulong id, ulong parentId, string path, string name, bool isDirectory, DateTime writeTime)
    {
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var lower = name.ToLowerInvariant();
        return new FileEntry
        {
            Id = id,
            ParentId = parentId,
            Name = name,
            IsDirectory = isDirectory,
            WriteTime = writeTime,
            Path = path,
            Folder = folder,
            Lower = lower,
            Initials = Pinyin.Initials(name),
            Pinyin = Pinyin.HasHan(name) ? Pinyin.Full(name) : lower
        };
    }

    private static DateTime FileTime(long ticks)
    {
        try { return ticks > 0 ? DateTime.FromFileTime(ticks) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    // ───────────────────────────── Delta application ─────────────────────────────

    /// <summary>
    /// Applies journal records. Idempotent: replaying a batch is always safe, which is what makes
    /// the persisted anchor the only state that matters after a crash.
    /// </summary>
    public int ApplyUsn(IReadOnlyList<UsnRecord> records, IndexRules rules) => ApplyJournal(records, rules);

    private int ApplyJournal(IReadOnlyList<UsnRecord> records, IndexRules rules)
    {
        var changed = ApplyLocked(records, rules);
        if (changed > 0)
        {
            lock (_gate) _dirty = true;
            Changed?.Invoke();
        }
        return changed;
    }

    private int ApplyLocked(IReadOnlyList<UsnRecord> records, IndexRules rules)
    {
        var changed = 0;
        lock (_gate)
        {
            // Renames arrive as OLD then NEW; only NEW is actionable (the record carries the new
            // parent and name), so a single pass over records is enough and stays idempotent.
            foreach (var record in records)
            {
                if ((record.Reason & UsnApi.UsnActionableMask) == 0) continue;
                if (record.Name.Length == 0 || record.Name.StartsWith('.')) continue;

                if ((record.Reason & UsnApi.USN_REASON_FILE_DELETE) != 0)
                {
                    changed += RemoveSubtreeLocked(record.FileId);
                    continue;
                }

                if ((record.Reason & UsnApi.USN_REASON_RENAME_OLD_NAME) != 0
                    && (record.Reason & UsnApi.USN_REASON_RENAME_NEW_NAME) == 0)
                    continue;

                // Parent must already be known: if it is not, this row moved out of the indexed
                // roots (or an ancestor was filtered out), so it no longer belongs here.
                if (!TryResolveParentLocked(record.ParentId, out var parentPath))
                {
                    if (_byId.ContainsKey(record.FileId)) changed += RemoveSubtreeLocked(record.FileId);
                    continue;
                }

                var isDirectory = record.IsDirectory;
                var path = Path.Combine(parentPath, record.Name);
                if (record.IsReparsePoint && isDirectory) continue;

                if (!rules.Accepts(path, record.Name, isDirectory, record.Attributes))
                {
                    // A rename can move a row out of the filter (e.g. .docx → .tmp).
                    if (_byId.ContainsKey(record.FileId)) { RemoveSubtreeLocked(record.FileId); changed++; }
                    continue;
                }

                var existed = _byId.TryGetValue(record.FileId, out var existing);
                if (existed && existing!.IsDirectory != isDirectory)
                {
                    RemoveSubtreeLocked(record.FileId);
                    existed = false;
                }

                var writeTime = record.TimeStamp > 0
                    ? FileTime(record.TimeStamp)
                    : existed ? existing!.WriteTime : DateTime.MinValue;

                if (existed)
                {
                    if (existing!.ParentId != record.ParentId
                        || !string.Equals(existing.Name, record.Name, StringComparison.Ordinal)
                        || !string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        ReparentLocked(existing, record.ParentId, record.Name, path);
                        changed++;
                    }
                    else if (writeTime > existing.WriteTime)
                    {
                        existing.WriteTime = writeTime;
                        changed++;
                    }
                    continue;
                }

                var entry = MakeEntry(record.FileId, record.ParentId, path, record.Name, isDirectory, writeTime);
                AddLocked(entry);
                changed++;

                // A directory that just appeared (or was moved in) brings its contents with it;
                // the journal only reports the directory itself.
                if (isDirectory) changed += PopulateSubtreeLocked(entry, rules);
            }

            if (changed > 0) { _searchListDirty = true; RebuildStatsLocked(); }
        }
        return changed;
    }

    /// <summary>Watcher-sourced change: the path is known, the IDs come from the file itself.</summary>
    public int ApplyPathChanges(IReadOnlyList<PathChange> changes, IndexRules rules)
    {
        var changed = 0;
        lock (_gate)
        {
            foreach (var change in changes)
            {
                if (change.Deleted)
                {
                    if (_byPath.TryGetValue(change.Path, out var gone)) changed += RemoveSubtreeLocked(gone.Id);
                    continue;
                }

                var name = Path.GetFileName(change.Path);
                if (name.Length == 0 || name.StartsWith('.')) continue;

                System.IO.FileAttributes attributes;
                try { attributes = System.IO.File.GetAttributes(change.Path); }
                catch { continue; }

                var isDirectory = (attributes & System.IO.FileAttributes.Directory) != 0;
                if (isDirectory && (attributes & System.IO.FileAttributes.ReparsePoint) != 0) continue;

                using var handle = isDirectory ? UsnApi.OpenDirectory(change.Path) : UsnApi.OpenFile(change.Path);
                if (handle.IsInvalid) continue;
                if (!UsnApi.TryGetIdentity(handle, out var id, out _, out var ticks)) continue;

                if (!rules.Accepts(change.Path, name, isDirectory, (uint)attributes))
                {
                    if (_byId.ContainsKey(id)) changed += RemoveSubtreeLocked(id);
                    continue;
                }

                if (_byId.TryGetValue(id, out var existing))
                {
                    if (!string.Equals(existing.Path, change.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        var parentPath = Path.GetDirectoryName(change.Path) ?? string.Empty;
                        ReparentLocked(existing, ParentIdForPathLocked(parentPath), name, change.Path);
                        changed++;
                    }
                    else
                    {
                        existing.WriteTime = FileTime(ticks);
                        changed++;
                    }
                    continue;
                }

                var folder = Path.GetDirectoryName(change.Path) ?? string.Empty;
                var entry = MakeEntry(id, ParentIdForPathLocked(folder), change.Path, name, isDirectory, FileTime(ticks));
                AddLocked(entry);
                changed++;
                if (isDirectory) changed += PopulateSubtreeLocked(entry, rules);
            }

            if (changed > 0) { _searchListDirty = true; RebuildStatsLocked(); }
        }

        if (changed > 0) Changed?.Invoke();
        return changed;
    }

    /// <summary>Reads a freshly appeared directory so its existing contents join the index.</summary>
    private int PopulateSubtreeLocked(FileEntry root, IndexRules rules)
    {
        var added = 0;
        var pending = new Queue<FileEntry>();
        pending.Enqueue(root);
        var guard = 0;

        while (pending.Count > 0 && guard++ < 20_000)
        {
            var folder = pending.Dequeue();
            using var handle = UsnApi.OpenDirectory(folder.Path);
            if (handle.IsInvalid) continue;
            foreach (var child in UsnApi.ListChildren(handle, 5000, out _))
            {
                var path = Path.Combine(folder.Path, child.Name);
                if (child.IsDirectory && child.IsReparsePoint) continue;
                if (!rules.Accepts(path, child.Name, child.IsDirectory, child.Attributes)) continue;
                if (_byId.ContainsKey(child.Id)) continue;

                var entry = MakeEntry(child.Id, folder.Id, path, child.Name, child.IsDirectory, FileTime(child.WriteTicks));
                AddLocked(entry);
                added++;
                if (child.IsDirectory) pending.Enqueue(entry);
            }
        }
        return added;
    }

    private bool TryResolveParentLocked(ulong parentId, out string path)
    {
        path = string.Empty;
        if (_rootSeeds.TryGetValue(parentId, out var seed)) { path = seed; return true; }
        if (_byId.TryGetValue(parentId, out var parent)) { path = parent.Path; return true; }
        return false;
    }

    /// <summary>
    /// The id of the folder at <paramref name="path"/>. The root itself has no row, so it is
    /// answered from the root seeds — without which a file added straight into a root would end
    /// up parentless and be dropped on the next load.
    /// </summary>
    private ulong ParentIdForPathLocked(string parentPath)
    {
        if (_byPath.TryGetValue(parentPath, out var row)) return row.Id;
        foreach (var (id, path) in _rootSeeds)
            if (string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase)) return id;
        return 0;
    }

    private void AddLocked(FileEntry entry)
    {
        _byId[entry.Id] = entry;
        _byPath[entry.Path] = entry;
        // One list per *parent*, not per row: this is what makes a folder rename update its
        // whole subtree without scanning the index.
        if (!_childIds.TryGetValue(entry.ParentId, out var siblings))
        {
            siblings = new List<ulong>();
            _childIds[entry.ParentId] = siblings;
        }
        if (!siblings.Contains(entry.Id)) siblings.Add(entry.Id);
    }

    private int RemoveSubtreeLocked(ulong id)
    {
        if (!_byId.TryGetValue(id, out var entry)) return 0;
        var removed = 0;
        var pending = new Queue<ulong>();
        pending.Enqueue(id);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!_byId.Remove(current, out var node)) continue;
            _byPath.Remove(node.Path);
            if (_childIds.TryGetValue(current, out var children))
            {
                foreach (var child in children) pending.Enqueue(child);
                _childIds.Remove(current);
            }
            if (_childIds.TryGetValue(node.ParentId, out var siblings)) siblings.Remove(current);
            removed++;
        }

        _searchListDirty = true;
        return removed;
    }

    /// <summary>Moves (and/or renames) a row, then rewrites the paths of everything beneath it.</summary>
    private void ReparentLocked(FileEntry entry, ulong newParentId, string newName, string newPath)
    {
        _byPath.Remove(entry.Path);
        if (_childIds.TryGetValue(entry.ParentId, out var oldSiblings)) oldSiblings.Remove(entry.Id);
        if (_childIds.TryGetValue(newParentId, out var newSiblings))
        {
            if (!newSiblings.Contains(entry.Id)) newSiblings.Add(entry.Id);
        }
        else _childIds[newParentId] = new List<ulong> { entry.Id };

        var changedName = !string.Equals(entry.Name, newName, StringComparison.Ordinal);
        if (changedName)
        {
            // Name is init-only on the row; replace it in place to keep the id/parent stable.
            var replacement = MakeEntry(entry.Id, newParentId, newPath, newName, entry.IsDirectory, entry.WriteTime);
            _byId[entry.Id] = replacement;
            _byPath[newPath] = replacement;
            CascadePathsLocked(replacement);
        }
        else
        {
            _byPath[newPath] = entry;
            if (!string.Equals(entry.Path, newPath, StringComparison.OrdinalIgnoreCase))
            {
                entry.Path = newPath;
                entry.Folder = Path.GetDirectoryName(newPath) ?? string.Empty;
                CascadePathsLocked(entry);
            }
        }

        _searchListDirty = true;
    }

    private void CascadePathsLocked(FileEntry root)
    {
        if (!_childIds.TryGetValue(root.Id, out var children)) return;
        var pending = new Queue<FileEntry>();
        foreach (var childId in children.ToArray())
            if (_byId.TryGetValue(childId, out var child)) pending.Enqueue(child);

        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            var parentPath = _byId.TryGetValue(node.ParentId, out var parent) ? parent.Path : node.Folder;
            var newPath = Path.Combine(parentPath, node.Name);
            if (!string.Equals(node.Path, newPath, StringComparison.OrdinalIgnoreCase))
            {
                _byPath.Remove(node.Path);
                node.Path = newPath;
                node.Folder = parentPath;
                _byPath[newPath] = node;
            }
            if (_childIds.TryGetValue(node.Id, out var grandChildren))
                foreach (var grandChildId in grandChildren.ToArray())
                    if (_byId.TryGetValue(grandChildId, out var grandChild)) pending.Enqueue(grandChild);
        }
    }

    // ───────────────────────────── Whole-index operations ─────────────────────────────

    private void ReplaceAllLocked(List<FileEntry> entries, IndexRules rules, IndexMeta meta)
    {
        _byId.Clear();
        _byPath.Clear();
        _childIds.Clear();
        _rootSeeds.Clear();

        foreach (var (path, id) in meta.Roots) _rootSeeds[id] = path;

        foreach (var entry in entries)
        {
            if (entry.Id == 0) { _byId[NextSyntheticId()] = entry; _byPath[entry.Path] = entry; continue; }
            AddLocked(entry);
        }

        Meta = meta;
        ResolvePathsLocked();
        _searchListDirty = true;
        RebuildStatsLocked();
        _ = rules;
    }

    /// <summary>
    /// Rebuilds every path from the id chain (parent name + "\" + name). This is what makes a
    /// persisted index survive a restart, and what lets a folder rename move its children
    /// without any of them being reported individually.
    /// </summary>
    private void ResolvePathsLocked()
    {
        var orphans = new List<ulong>();

        foreach (var entry in _byId.Values.ToArray())
        {
            if (entry.Path.Length != 0 || entry.Id == 0) continue;

            var chain = new List<FileEntry>();
            var current = entry;
            string? basePath = null;

            for (var guard = 0; guard < 512; guard++)
            {
                if (current.Path.Length != 0) { basePath = current.Path; break; }
                if (_rootSeeds.TryGetValue(current.Id, out var seed)) { basePath = seed; break; }
                chain.Add(current);
                if (!_byId.TryGetValue(current.ParentId, out var parent))
                {
                    // The chain ends at a root: its own row is not part of the index.
                    if (_rootSeeds.TryGetValue(current.ParentId, out var parentSeed)) basePath = parentSeed;
                    break;
                }
                current = parent;
            }

            if (basePath is null) { orphans.Add(entry.Id); continue; }

            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var node = chain[i];
                node.Path = Path.Combine(basePath, node.Name);
                node.Folder = basePath;
                basePath = node.Path;
                node.Lower = node.Name.ToLowerInvariant();
                node.Initials = Pinyin.Initials(node.Name);
                _byPath[node.Path] = node;
            }
        }

        // Rows whose ancestors are not in the index cannot be placed under any root.
        foreach (var id in orphans)
        {
            if (!_byId.Remove(id, out var orphan)) continue;
            _byPath.Remove(orphan.Path);
        }
    }

    /// <summary>Merges shell app rows into the live collection (they are never persisted).</summary>
    private void AppendAppRowsLocked()
    {
        var apps = new List<FileEntry>();
        MergeStoreApps(apps);
        var existing = new HashSet<string>(_byId.Values.Select(entry => entry.Lower), StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (!existing.Add(app.Lower)) continue;
            _byId[NextSyntheticId()] = app;
            _byPath[app.Path] = app;
        }
        _searchListDirty = true;
    }

    private ulong _syntheticId = ulong.MaxValue;
    private ulong NextSyntheticId() => _syntheticId--;

    /// <summary>
    /// Drops every file of one extension from the index and from the stats. The settings list's
    /// per-row delete also removes the extension from the whitelist, so a rebuild will not bring
    /// the rows back.
    /// </summary>
    public void RemoveExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return;
        var ext = extension.StartsWith('.') ? extension : "." + extension;

        lock (_gate)
        {
            var doomed = _byId.Values
                .Where(entry => !entry.IsDirectory && !IsAppEntry(entry)
                    && string.Equals(Path.GetExtension(entry.Path), ext, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Id)
                .ToList();
            foreach (var id in doomed) RemoveSubtreeLocked(id);
            RebuildStatsLocked();
            _searchListDirty = true;
            _dirty = true;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Drops rows whose path no longer exists. A closed app or an overflowed watcher buffer can
    /// miss a delete, and this is the cheap way to correct it without a full rebuild: the caller
    /// has already touched the filesystem, so it knows they are gone.
    /// </summary>
    public int RemovePaths(IEnumerable<string> paths, bool notify = true)
    {
        var removed = 0;
        lock (_gate)
        {
            foreach (var path in paths)
                if (_byPath.TryGetValue(path, out var entry))
                    removed += RemoveSubtreeLocked(entry.Id);
            if (removed > 0)
            {
                RebuildStatsLocked();
                _searchListDirty = true;
                _dirty = true;
            }
        }
        if (removed > 0 && notify) Changed?.Invoke();
        return removed;
    }

    // ───────────────────────────── Persistence ─────────────────────────────

    private const string Magic = "JIDX3";
    private string? IndexPath => _directory is null ? null : Path.Combine(_directory, "index.bin");

    /// <summary>Why the last <see cref="Load"/> failed (diagnostics only).</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>Loads the persisted index. Returns false when absent or built by another rule set.</summary>
    public bool Load(IndexRules rules)
    {
        var path = IndexPath;
        if (path is null || !File.Exists(path)) { LastLoadError = "missing"; return false; }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadString() != Magic) { LastLoadError = "magic"; return false; }
            var metaJson = reader.ReadString();
            var meta = JsonSerializer.Deserialize<IndexMeta>(metaJson);
            if (meta is null || !meta.Complete) { LastLoadError = "incomplete"; return false; }
            if (meta.RuleKey != rules.Key) { LastLoadError = "rules"; return false; }
            // JSON rebuilds the dictionaries with a case-sensitive comparer; restore the
            // case-insensitive one so drive/root lookups behave like they do in memory.
            meta.Roots = new Dictionary<string, ulong>(meta.Roots, StringComparer.OrdinalIgnoreCase);
            meta.Volumes = new Dictionary<string, VolumeAnchor>(meta.Volumes, StringComparer.OrdinalIgnoreCase);

            var count = reader.ReadInt32();
            var entries = new List<FileEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var id = reader.ReadUInt64();
                var parentId = reader.ReadUInt64();
                var ticks = reader.ReadInt64();
                var flags = reader.ReadByte();
                var name = reader.ReadString();
                var lower = name.ToLowerInvariant();
                var entry = new FileEntry
                {
                    Id = id,
                    ParentId = parentId,
                    Name = name,
                    IsDirectory = (flags & 1) != 0,
                    WriteTime = FileTime(ticks),
                    Path = string.Empty,
                    Folder = string.Empty,
                    Lower = lower,
                    Initials = Pinyin.Initials(name),
                    Pinyin = Pinyin.HasHan(name) ? Pinyin.Full(name) : lower
                };
                entries.Add(entry);
            }

            lock (_gate)
            {
                ReplaceAllLocked(entries, rules, meta);
                // App rows are derived from the shell, not from disk, so they are rebuilt here
                // instead of being stored (their synthetic path would not survive a round-trip).
                AppendAppRowsLocked();
                LastBuilt = DateTime.TryParse(meta.BuiltAt, out var built) ? built : DateTime.MinValue;
                _dirty = false;
            }
            LastLoadError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastLoadError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>Writes rows and metadata atomically, then clears the dirty flag.</summary>
    public void Save()
    {
        var path = IndexPath;
        if (path is null) return;

        List<FileEntry> rows;
        IndexMeta meta;
        lock (_gate)
        {
            if (!Meta.Complete || _byId.Count == 0) return;
            rows = new List<FileEntry>(_byId.Count);
            // App rows have no file id and no on-disk path: they are re-merged on load.
            foreach (var entry in _byId.Values)
                if (entry.Id != 0) rows.Add(entry);
            meta = Meta;
            meta.BuiltAt = DateTime.Now.ToString("O");
        }

        try
        {
            Directory.CreateDirectory(_directory!);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(JsonSerializer.Serialize(meta));
                writer.Write(rows.Count);
                foreach (var entry in rows)
                {
                    writer.Write(entry.Id);
                    writer.Write(entry.ParentId);
                    writer.Write(entry.WriteTime > DateTime.MinValue ? entry.WriteTime.ToFileTime() : 0L);
                    writer.Write((byte)(entry.IsDirectory ? 1 : (entry.Id == 0 ? 2 : 0)));
                    writer.Write(entry.Name);
                }
            }
            File.Move(temp, path, overwrite: true);
            lock (_gate) _dirty = false;
        }
        catch (Exception ex)
        {
            LastLoadError = "save: " + ex.Message;
        }
    }

    /// <summary>Updates the persisted journal position for one volume.</summary>
    public void SetAnchor(string volume, VolumeAnchor anchor)
    {
        lock (_gate)
        {
            Meta.Volumes[volume] = anchor;
            _dirty = true;
        }
    }

    public VolumeAnchor? GetAnchor(string volume)
    {
        lock (_gate) return Meta.Volumes.TryGetValue(volume, out var anchor) ? anchor : null;
    }

    public IReadOnlyList<string> IndexedRoots
    {
        get { lock (_gate) return Meta.Roots.Keys.ToList(); }
    }

    /// <summary>Marks the stored rows as filtered by an outdated rule set.</summary>
    public void MarkStale()
    {
        lock (_gate)
        {
            Meta.Complete = false;
            _dirty = true;
        }
    }

    // ───────────────────────────── App rows ─────────────────────────────

    private const string FileEntryAppsPrefix = "shell:AppsFolder\\";

    /// <summary>
    /// Launchable apps join the index as rows: the walk covers classic .lnk software through the
    /// Start Menu, this merge covers 便签 / 录音机 style Windows apps that have no .lnk at all.
    /// </summary>
    private static void MergeStoreApps(List<FileEntry> into)
    {
        try
        {
            var apps = AppsFolderService.Enumerate();
            var seen = new HashSet<string>(into.Select(entry => entry.Lower), StringComparer.OrdinalIgnoreCase);

            foreach (var app in apps)
            {
                var lower = app.Name.ToLowerInvariant();
                if (lower.Length == 0 || !seen.Add(lower)) continue;
                into.Add(new FileEntry
                {
                    Id = 0,
                    ParentId = 0,
                    Name = app.Name,
                    IsDirectory = false,
                    WriteTime = DateTime.MinValue,
                    Path = FileEntryAppsPrefix + app.AumId,
                    Folder = "已安装应用",
                    Lower = lower,
                    Initials = Pinyin.Initials(app.Name)
                });
            }
        }
        catch { /* Shell unavailable — file results still work */ }
    }

    /// <summary>True for the app rows merged by <see cref="MergeStoreApps"/>.</summary>
    public static bool IsAppEntry(FileEntry entry) =>
        entry.Path.StartsWith(FileEntryAppsPrefix, StringComparison.OrdinalIgnoreCase);

    public static string AppAumId(FileEntry entry) => entry.Path[FileEntryAppsPrefix.Length..];

    /// <summary>
    /// Desktop, Documents, Downloads, Pictures, and both Start Menu folders. Fixed drive roots
    /// get a separate shallow discovery pass.
    /// </summary>
    public static List<string> DefaultRoots()
    {
        var roots = new List<string>();
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyPictures
                 })
        {
            var path = Environment.GetFolderPath(folder);
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

/// <summary>A change reported by a directory watcher, already reduced to "this path changed".</summary>
public readonly record struct PathChange(string Path, bool Deleted);
