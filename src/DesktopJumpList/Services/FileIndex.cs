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

    /// <summary>
    /// Lower-case full path with backslash separators — the string every search scans. Storing it
    /// (instead of lower-casing Path on each keystroke) is the space-for-time trade: ~2 bytes per
    /// character per entry, and the scan becomes a few IndexOf calls on an existing string.
    /// </summary>
    /// <summary>
    /// The string search actually scans: lower-cased path, with each Han segment followed by its
    /// pinyin (see <see cref="Pinyin.SearchForm"/>). One scan therefore answers "新建文件夹",
    /// "xinjianwenjianjia" and mixtures of the two.
    /// </summary>
    public string PathSearch { get; set; } = string.Empty;

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
    // 4: a wildcard exclusion now also matches the folder itself ("*\Temp\*" covers …\Temp),
    //    so indexes built by 3 still carry folder rows that are no longer allowed.
    private const int RulesVersion = 4;

    // Windows attribute bits used by the built-in exclusions (System.IO.FileAttributes has the
    // same values, but keeping them local avoids casting noise at every call site).
    private const uint AttributeHidden = 0x00000002;
    private const uint AttributeSystem = 0x00000004;

    private string BuildKey()
    {
        var parts = new List<string>
        {
            "v" + RulesVersion,
            "index=" + Infrastructure.BuildInfo.IndexVersion,
            "limit=" + Limit,
            "drives=" + (DiscoverDriveRoots ? 1 : 0)
        };
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
        if (normalized.Length == 2 && normalized[1] == ':') return normalized + "\\";
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
                // "*\Temp\*" is written for the *contents* of a Temp folder, so the folder row
                // itself slipped through and showed up in results. Test it as "…\Temp\" too.
                if (path.Length > 0 && path[^1] != '\\' && MatchesWildcard(path + "\\", entry)) return true;
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

    /// <summary>Structure version this file was written with (0 = written before it existed).</summary>
    public int IndexVersion { get; set; }
    public string BuiltAt { get; set; } = string.Empty;
    public bool Complete { get; set; }

    /// <summary>root path → its own file id, so a child's path can be resolved from its chain.</summary>
    public Dictionary<string, ulong> Roots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>volume root ("C:\") → journal anchor after the last successfully applied batch.</summary>
    public Dictionary<string, VolumeAnchor> Volumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>root path → how many rows the last walk took from it (build report).</summary>
    public Dictionary<string, int> RootCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>root path → why it contributed nothing, when it did. Empty is the normal case.</summary>
    public Dictionary<string, string> RootNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Newest-first order for the "/s" empty query, rebuilt only when the contents move.</summary>
    private List<FileEntry>? _newest;
    private int _newestVersion = -1;
    // Typing narrows: a longer query can only return fewer entries, so the previous result set is
    // the only thing the next keystroke has to look at. Sound because every added character either
    // extends a token or adds one, and both can only remove matches.
    private string _narrowKey = string.Empty;
    private List<FileEntry>? _narrowSet;
    private QueryTokens? _narrowTokens;
    private int _contentVersion;
    // 扩展名 → 条数，增量维护：一次批处理只碰变更的条目，不再遍历整个索引。
    // 读数时（设置页的「索引数据」）才排序，排序不落在写入路径上。
    private readonly Dictionary<string, int> _extensionCounts = new(StringComparer.OrdinalIgnoreCase);
    private bool _searchListDirty = true;

    private CancellationTokenSource? _cancellation;
    private readonly string? _directory;
    private bool _dirty;
    private int _dirtyBatches;

    /// <summary>Bumped by <see cref="MarkStale"/>; a walk that started before it is discarded.</summary>
    private int _staleStamp;

    /// <summary>The rule set the current rows are supposed to honour.</summary>
    private string? _activeRuleKey;

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

    /// <summary>
    /// Human-readable result of the last rebuild: why a root stayed empty, or that the walk was
    /// discarded because the rules changed under it. Empty when everything went normally.
    /// </summary>
    public string LastBuildNote { get; private set; } = string.Empty;

    /// <summary>Per-root row counts of the last build, for the "索引已更新" report.</summary>
    public IReadOnlyDictionary<string, int> RootCounts
    {
        get { lock (_gate) return new Dictionary<string, int>(Meta.RootCounts, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Roots that contributed nothing and why — a drive that quietly indexes nothing.</summary>
    public IReadOnlyDictionary<string, string> RootNotes
    {
        get { lock (_gate) return new Dictionary<string, string>(Meta.RootNotes, StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Raised after any change to the contents, from whichever thread made it.</summary>
    public event Action? Changed;

    /// <summary>
    /// What the user has actually opened from the search box. Optional: without it the ranking
    /// falls back to "newest first" like before, but with it a file you launch every day stops
    /// hiding behind yesterday's download.
    /// </summary>
    public UsageStore? Usage { get; set; }

    public bool HasIndex
    {
        get { lock (_gate) return _byId.Count > 0 && Meta.Complete; }
    }

    public bool IsDirty
    {
        get { lock (_gate) return _dirty; }
    }

    /// <summary>How many change batches piled up since the last write — the save throttle reads it.</summary>
    public int DirtyBatches
    {
        get { lock (_gate) return _dirtyBatches; }
    }

    public long MemoryBytes
    {
        get
        {
            lock (_gate)
            {
                long bytes = 0;
                foreach (var entry in _byId.Values)
                    bytes += (entry.Path.Length + entry.PathSearch.Length + entry.Name.Length
                              + entry.Folder.Length + entry.Lower.Length + entry.Initials.Length
                              + entry.Pinyin.Length) * 2 + 180;
                return bytes;
            }
        }
    }

    // ───────────────────────────── Queries ─────────────────────────────

    /// <summary>Marks the flat search list stale and invalidates the narrowing cache.</summary>
    private void MarkSearchDirtyLocked()
    {
        _searchListDirty = true;
        _contentVersion++;
        _narrowSet = null;
        _narrowTokens = null;
        _narrowKey = string.Empty;
        _dirtyBatches++;
        _dirty = true;
    }

    /// <summary>
    /// A new entry can join the result set of the query the user is still typing, so the narrowing
    /// cache is extended instead of thrown away — O(1) per added file instead of forcing the next
    /// keystroke back to a full scan.
    /// </summary>
    private void AppendToNarrowSetLocked(FileEntry entry)
    {
        if (_narrowSet is not { } set || _narrowTokens is not { } tokens) return;
        if (set.Count >= NarrowCacheLimit) return;
        if (!TryMatch(entry, tokens, out _, out _)) return;
        set.Add(entry);
    }

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

    /// <summary>
    /// Hidden `--index-bench` driver: seeds N synthetic rows through the real add path, then times
    /// the operations that run on every delta batch. Returns a multi-line report.
    /// </summary>
    internal void BenchReport(int count)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        lock (_gate)
        {
            _searchListDirty = false;
            _searchList.Clear();
            for (var i = 1; i <= count; i++)
            {
                var parent = (ulong)(i / 50) + 1;
                var name = $"file{i:0000000}.txt";
                var path = $@"c://bench//dir{i / 50:0000}//{name}";
                AddLocked(MakeEntry((ulong)i, parent, path, name, false, DateTime.UtcNow.AddMinutes(-i)));
            }
            MarkSearchDirtyLocked();
        }
        Console.WriteLine($"seed {count} rows: {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        SnapshotLocked();
        Console.WriteLine($"snapshot rebuild: {sw.ElapsedMilliseconds} ms");

        // 每次击键一条：前缀越来越长，最容易触发全量扫描
        foreach (var query in new[] { "f", "fi", "fil", "file", "file12", "file1234" })
        {
            sw.Restart();
            var hits = Search(query, 31);
            Console.WriteLine($"search [{query}] -> {hits.Count} hits: {sw.ElapsedMilliseconds} ms");
        }

        sw.Restart();
        lock (_gate)
        {
            for (var i = 0; i < 1000; i++)
            {
                var id = (ulong)(count + 1 + i);
                var name = $"added{i:0000}.txt";
                AddLocked(MakeEntry(id, 1, $@"c://bench//dir0001//{name}", name, false, DateTime.UtcNow));
            }
            MarkSearchDirtyLocked();
        }
        Console.WriteLine($"1000 adds (one batch): {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        for (var i = 0; i < 1000; i++)
        {
            lock (_gate) RemoveSubtreeLocked((ulong)(count + 1 + i));
        }
        Console.WriteLine($"1000 removes (1000 batches): {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        for (var i = 0; i < 1000; i++)
        {
            lock (_gate)
            {
                if (!_byId.TryGetValue((ulong)(10 + i), out var entry)) continue;
                ReparentLocked(entry, entry.ParentId, $"renamed{i:0000}.txt",
                    $@"c://bench//dir{entry.ParentId:0000}//renamed{i:0000}.txt");
            }
        }
        Console.WriteLine($"1000 renames: {sw.ElapsedMilliseconds} ms");

        Console.WriteLine($"extension histogram: {_extensionCounts.Count} kinds");
        Console.WriteLine($"memory estimate: {MemoryBytes / (1024.0 * 1024.0):0.0} MB");
    }

    /// <summary>A handful of entries for "/s" with no keyword yet: newest touched first.</summary>
    /// <remarks>
    /// Files only. A folder's write time moves whenever anything inside it changes, so a
    /// newest-first list that includes folders is a list of folders — the file the user just
    /// saved ends up below six directory rows that were "touched" by something else.
    /// </remarks>
    public List<FileEntry> Take(int max, int skip = 0)
    {
        if (max <= 0) return new List<FileEntry>();
        List<FileEntry> newest;
        lock (_gate)
        {
            // "/s" pages once per scroll; re-sorting 100k rows on every page was pure waste.
            if (_newest is null || _newestVersion != _contentVersion)
            {
                _newest = new List<FileEntry>(_byId.Count);
                foreach (var entry in _byId.Values)
                    if (!entry.IsDirectory) _newest.Add(entry);
                _newest.Sort((a, b) => b.WriteTime.CompareTo(a.WriteTime));
                _newestVersion = _contentVersion;
            }
            newest = _newest;
        }

        var start = Math.Clamp(skip, 0, newest.Count);
        return newest.GetRange(start, Math.Min(max, newest.Count - start));
    }

    /// <summary>
    /// Scores every entry against the query. Words are AND-ed but **order-free**: "2026 报告" and
    /// "报告 2026" are the same question, and a launcher has to answer both. The previous version
    /// scanned the path left to right, which silently demanded that the words appear in path order
    /// — the same two words found 14 files one way and 1 the other.
    /// </summary>
    /// <param name="cancellation">Checked every 1024 entries; a superseded keystroke stops early.</param>
    public List<FileEntry> Search(string query, int max, int skip = 0, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(query) || max <= 0) return new List<FileEntry>();

        var tokens = QueryTokens.Parse(query);
        if (tokens.Length == 0) return new List<FileEntry>();

        var hits = new List<Hit>();
        List<FileEntry> source;
        var version = 0;
        lock (_gate)
        {
            version = _contentVersion;
            // A longer query against the previous result set is enough — see _narrowSet.
            source = _narrowSet is { Count: > 0 } previous
                     && tokens.Normalized.StartsWith(_narrowKey, StringComparison.Ordinal)
                ? previous
                : SnapshotLocked();
        }

        var usage = Usage;
        var scanned = 0;
        foreach (var entry in source)
        {
            // Throw instead of returning a short list: "cancelled" must never be mistaken for
            // "no hits", or a superseded keystroke would truncate the list on screen.
            if ((++scanned & 0x3FF) == 0) cancellation.ThrowIfCancellationRequested();
            if (!TryMatch(entry, tokens, out var score, out var position)) continue;
            hits.Add(new Hit(entry, score, position, usage?.Boost(entry.Path) ?? 0));
        }

        var start = Math.Max(0, skip);

        // A query like "e" matches half the disk: keeping every hit for the next keystroke and
        // sorting all of them is wasted work when the caller only ever looks at one page.
        if (hits.Count > NarrowCacheLimit)
            return SelectBest(hits, start + max, start, max);

        hits.Sort(HitOrder);
        var ordered = new List<FileEntry>(hits.Count);
        foreach (var hit in hits) ordered.Add(hit.Entry);

        // Remember this result set for the next keystroke — only while the index has not moved.
        lock (_gate)
        {
            if (_contentVersion == version)
            {
                _narrowKey = tokens.Normalized;
                _narrowSet = ordered;
                _narrowTokens = tokens;
            }
        }

        if (start >= ordered.Count) return new List<FileEntry>();
        return ordered.GetRange(start, Math.Min(max, ordered.Count - start));
    }

    /// <summary>
    /// The best <paramref name="k"/> hits without sorting the rest: a small sorted window where
    /// the last slot is the worst kept hit, so a candidate that loses to it costs one comparison.
    /// </summary>
    private static List<FileEntry> SelectBest(List<Hit> hits, int k, int skip, int max)
    {
        if (k <= 0 || max <= 0) return new List<FileEntry>();

        var window = new Hit[Math.Min(k, hits.Count)];
        var filled = 0;
        foreach (var hit in hits)
        {
            if (filled == window.Length)
            {
                // A full window only changes when the candidate beats the worst one in it.
                if (HitOrder(hit, window[filled - 1]) >= 0) continue;
            }

            var at = filled < window.Length ? filled++ : window.Length - 1;
            while (at > 0 && HitOrder(hit, window[at - 1]) < 0)
            {
                window[at] = window[at - 1];
                at--;
            }
            window[at] = hit;
        }

        var rows = new List<FileEntry>(Math.Min(max, Math.Max(0, filled - skip)));
        for (var i = skip; i < filled && rows.Count < max; i++) rows.Add(window[i].Entry);
        return rows;
    }

    /// <summary>Above this many matches, keeping the set for the next keystroke is not worth it.</summary>
    private const int NarrowCacheLimit = 50_000;

    // ───────────────────────────── Score ladder ─────────────────────────────

    // Lower is better. The *name* owns 0–6 and everything that only matched through a folder
    // starts at 7: that single split is what stops a folder called "doc" from burying every file
    // whose name contains "doc", and what stops "…\bower-asset" from outranking "setup.exe".
    private const int ScoreNameExact = 0;
    private const int ScoreNamePrefix = 1;
    private const int ScoreNameWord = 2;
    private const int ScoreNameContains = 3;
    private const int ScoreInitialsPrefix = 4;
    private const int ScoreInitialsContains = 5;
    private const int ScoreSubsequence = 6;
    private const int ScorePathSegment = 7;
    private const int ScorePathAnywhere = 8;

    /// <summary>A file is the better answer than a folder for the same text.</summary>
    private const int ScoreDirectory = 1;

    /// <summary>One scored row: <see cref="Score"/> ascending, <see cref="Boost"/> is usage weight.</summary>
    private readonly record struct Hit(FileEntry Entry, int Score, int Position, int Boost);

    private static readonly Comparison<Hit> HitOrder = (a, b) =>
    {
        var byScore = a.Score.CompareTo(b.Score);
        if (byScore != 0) return byScore;
        // What the user actually opens wins a tie — this is the only memory the index has of
        // "mine", and it is what makes a frequently used file float above its neighbours.
        var byUse = b.Boost.CompareTo(a.Boost);
        if (byUse != 0) return byUse;
        var byPosition = a.Position.CompareTo(b.Position);
        if (byPosition != 0) return byPosition;
        var byTime = b.Entry.WriteTime.CompareTo(a.Entry.WriteTime);
        return byTime != 0 ? byTime : a.Entry.Name.Length.CompareTo(b.Entry.Name.Length);
    };

    /// <summary>
    /// One entry against one prepared query: every word has to be accounted for, in any order,
    /// and the score of a word is the best place it was found (its own name beats a folder above
    /// it). <paramref name="position"/> sums the offsets so a shallower match wins a tie.
    /// </summary>
    private static bool TryMatch(FileEntry entry, in QueryTokens tokens, out int score, out int position)
    {
        score = 0;
        position = 0;
        foreach (var token in tokens.All)
        {
            if (!ScoreToken(entry, token, out var one, out var at)) return false;
            score += one;
            position += at;
        }

        if (entry.IsDirectory) score += ScoreDirectory;
        return true;
    }

    /// <summary>
    /// How well one word fits one entry. The name is asked first and in full (exact, prefix, word
    /// start, substring, initials/pinyin, abbreviation); only when the name says nothing do the
    /// folders above it get a say.
    /// </summary>
    private static bool ScoreToken(FileEntry entry, in QueryTokens.Token token, out int score, out int position)
    {
        score = 0;
        position = 0;

        var needle = token.Text;
        var name = entry.Lower;
        if (name.Length > 0)
        {
            // One IndexOf answers three questions: prefix, word start, plain substring. Doing it
            // twice (once for "starts a word", once for "is in there") doubled the scan cost.
            var at = name.IndexOf(needle, StringComparison.Ordinal);
            if (at == 0) { score = name.Length == needle.Length ? ScoreNameExact : ScoreNamePrefix; return true; }
            if (at > 0)
            {
                var wordStart = !char.IsLetterOrDigit(name[at - 1]);
                score = wordStart ? ScoreNameWord : ScoreNameContains;
                // ".png" matches every file of that type at *its own* offset, so the offset
                // only says "shorter name wins" — which puts 0.png, 1.png, 2.png above the
                // screenshot the user just saved. An extension carries no position signal;
                // let the tie fall through to recency.
                position = needle[0] == '.' ? 0 : at;
                return true;
            }

            // Compact forms: "vsc" from initials, "weixin" from full pinyin, "sjg" from 设计稿.
            if (entry.Initials.Length > 0)
            {
                if (entry.Initials.StartsWith(needle, StringComparison.Ordinal)) { score = ScoreInitialsPrefix; return true; }
                if (entry.Initials.Length > needle.Length && entry.Initials.Contains(needle, StringComparison.Ordinal))
                {
                    score = ScoreInitialsContains;
                    return true;
                }
            }

            // Full pinyin only exists for Han names; for ASCII the field *is* the lower name
            // (same instance), so this branch costs one reference check.
            if (!ReferenceEquals(entry.Pinyin, name) && entry.Pinyin.Length > 0)
            {
                if (entry.Pinyin.StartsWith(needle, StringComparison.Ordinal)) { score = ScoreInitialsPrefix; return true; }
                if (needle.Length >= 2 && entry.Pinyin.Contains(needle, StringComparison.Ordinal))
                {
                    score = ScoreInitialsContains;
                    return true;
                }
            }

            // Abbreviations typed as one word ("vscode"); short ones would match everything, and
            // the density floor in TextMatch keeps scattered letters out.
            if (needle.Length >= 3 && TextMatch.IsSubsequence(name, needle, out var gap))
            {
                score = ScoreSubsequence;
                position = Math.Min(gap, 4096);
                return true;
            }
        }

        // The folders above it: still a hit ("jdk" finds D:\jdk-17\bin\java.exe), just a worse one.
        var path = entry.PathSearch;
        if (path.Length == 0) return false;

        var segment = path.IndexOf("\\" + needle, StringComparison.Ordinal);
        if (segment >= 0) { score = ScorePathSegment; position = segment; return true; }

        var anywhere = path.IndexOf(needle, StringComparison.Ordinal);
        if (anywhere >= 0) { score = ScorePathAnywhere; position = anywhere; return true; }

        return false;
    }

    /// <summary>
    /// One query, prepared once per keystroke instead of once per entry: lower-case, '/' folded to
    /// '\\', split on whitespace and on the path separator, so "download/set", "download\set" and
    /// "download set" all become the same two-requirement query.
    /// </summary>
    private readonly struct QueryTokens
    {
        public readonly struct Token
        {
            public readonly string Text;

            public Token(string text) => Text = text;
        }

        public readonly Token[] All;

        /// <summary>The query as used for matching: lower-case, '/' folded, whitespace collapsed.</summary>
        public readonly string Normalized;

        private QueryTokens(Token[] all, string normalized)
        {
            All = all;
            Normalized = normalized;
        }

        public int Length => All.Length;

        public static QueryTokens Parse(string query)
        {
            // A pasted "C:/Users/…" and a typed "downloads\hermes.exe" have to mean the same thing.
            var normalized = query.Replace('/', '\\').Trim().ToLowerInvariant();
            var pieces = normalized.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            var tokens = new Token[pieces.Length];
            for (var i = 0; i < pieces.Length; i++) tokens[i] = new Token(pieces[i]);
            return new QueryTokens(tokens, normalized);
        }

        private static readonly char[] Separators = { ' ', '\t', '\r', '\n', '\\' };
    }

    public List<(string Extension, int Count)> GetExtensionStats()
    {
        lock (_gate)
            return _extensionCounts
                .OrderByDescending(pair => pair.Value)
                .Select(pair => (pair.Key, pair.Value))
                .ToList();
    }

    /// <summary>Full recount — only for the initial load, where every row arrives at once.</summary>
    private void RebuildStatsLocked()
    {
        _extensionCounts.Clear();
        foreach (var entry in _byId.Values) CountExtensionLocked(entry, +1);
    }

    /// <summary>Adds (or, with -1, removes) one entry's contribution to the extension histogram.</summary>
    private void CountExtensionLocked(FileEntry entry, int delta)
    {
        if (entry.IsDirectory || IsAppEntry(entry)) return;
        var extension = Path.GetExtension(entry.Path);
        if (extension.Length == 0) return;

        var next = _extensionCounts.GetValueOrDefault(extension) + delta;
        if (next > 0) _extensionCounts[extension] = next;
        else _extensionCounts.Remove(extension);
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
        _activeRuleKey = rules.Key;

        // The rules this walk is honouring. If they are replaced while the walk is in flight the
        // result must be thrown away: publishing it would mark an index of the *old* roots as
        // complete, wipe the "stale" flag and persist rows the next startup has to reject.
        var stamp = _staleStamp;

        Task.Run(() =>
        {
            UsnApi.EnterBackgroundMode();
            var built = Build(rules, token, out var meta, out var cancelled);
            var superseded = stamp != _staleStamp;
            if (!cancelled && !superseded)
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
                LastBuildNote = meta.RootNotes.Count == 0
                    ? string.Empty
                    : string.Join("；", meta.RootNotes.Select(pair => $"{pair.Key} {pair.Value}"));
                Save();
            }

            if (ReferenceEquals(_cancellation, cancellation)) IsBuilding = false;

            if (!cancelled && !superseded)
            {
                IconsWarm = true;
                Changed?.Invoke();
                onDone?.Invoke();
            }
            else if (superseded)
            {
                // The rules moved under us: leave the old rows searchable and let the rebuild the
                // caller already asked for replace them.
                LastBuildNote = "规则已变更，本次构建结果被丢弃";
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

                // A root that contributes nothing must say so: a silently empty drive looks
                // exactly like "the feature is broken", and that is what the user reports.
                if (!Directory.Exists(root))
                {
                    meta.RootNotes[root] = "目录不存在";
                    continue;
                }
                if (rules.IsExcluded(root))
                {
                    meta.RootNotes[root] = "被黑名单排除";
                    continue;
                }

                rules.PerRoot.TryGetValue(root, out var cfg);
                var depthCap = cfg is null || cfg.MaxDepth <= 0 ? int.MaxValue : cfg.MaxDepth;
                var entryCap = cfg is null || cfg.MaxEntries <= 0 ? int.MaxValue : cfg.MaxEntries;
                var before = found.Count;
                var note = WalkRoot(root, rules, depthCap, entryCap, cap, found, seeds, token);
                meta.RootCounts[root] = found.Count - before;
                if (note is not null) meta.RootNotes[root] = note;
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

    /// <summary>
    /// Walks one root. Returns a note when the root was skipped outright (so the build report can
    /// explain an empty drive instead of leaving the user guessing), null when it walked.
    /// </summary>
    private string? WalkRoot(string root, IndexRules rules, int depthCap, int entryCap, int globalLimit,
        List<FileEntry> into, Dictionary<ulong, string> seeds, CancellationToken token)
    {
        // A root that is itself a junction must be validated before anything under it is added.
        var realRoot = UsnApi.TryGetFinalPath(root);
        if (realRoot is not null && !string.Equals(realRoot, root, StringComparison.OrdinalIgnoreCase)
            && rules.IsExcluded(realRoot)) return "真实路径被黑名单排除";

        using var rootHandle = UsnApi.OpenDirectory(root);
        if (rootHandle.IsInvalid) return "无法打开目录";
        if (!UsnApi.TryGetIdentity(rootHandle, out var rootId, out _, out _)) return "无法读取目录标识";

        // The id is what a child's chain resolves against, and the *root directory of every
        // volume is MFT record 5* — so "C:\" and "D:\" routinely carry the same file reference
        // number. Without this guard the second walk steals the first root's seed and every
        // child of the first drive resolves its path against the wrong letter.
        var seedKey = rootId;
        while (seeds.TryGetValue(seedKey, out var claimed)
               && !string.Equals(claimed, root, StringComparison.OrdinalIgnoreCase))
            seedKey = NextSeedId();
        seeds[seedKey] = root;

        var pending = new Stack<(string Path, ulong Id, int Depth)>();
        pending.Push((root, seedKey, 0));
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

        return null;
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
                _ = WalkRoot(path, rules, depthCap: 0, entryCap: int.MaxValue, globalLimit, into, seeds, token);
            }
            catch { /* an unavailable drive is skipped */ }
        }
    }

    private static FileEntry MakeEntry(ulong id, ulong parentId, string path, string name, bool isDirectory, DateTime writeTime)
    {
        var lower = name.ToLowerInvariant();
        var entry = new FileEntry
        {
            Id = id,
            ParentId = parentId,
            Name = name,
            IsDirectory = isDirectory,
            WriteTime = writeTime,
            Lower = lower,
            Initials = Pinyin.Initials(name),
            Pinyin = Pinyin.HasHan(name) ? Pinyin.Full(name) : lower
        };
        return WithPath(entry, path);
    }

    /// <summary>
    /// Writes the three path-derived strings together. Every place that moves a node goes through
    /// here: PathLow and PathPinyin are what search scans, so they must never drift from Path.
    /// </summary>
    private static FileEntry WithPath(FileEntry entry, string path)
    {
        entry.Path = path;
        entry.PathSearch = Pinyin.SearchForm(path);
        entry.Folder = Path.GetDirectoryName(path) ?? string.Empty;
        return entry;
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
        if (!AcceptsDeltasLocked(rules)) return 0;
        var changed = ApplyLocked(records, rules);
        if (changed > 0)
        {
            lock (_gate) _dirty = true;
            Changed?.Invoke();
        }
        return changed;
    }

    /// <summary>
    /// A delta is only meaningful for the rule set the rows were built with. Applying watcher
    /// notifications that belong to *new* rules (a root the walk has not visited yet) is what
    /// created rows whose parent was never indexed: searchable once, gone after a restart.
    /// The pending rebuild is what covers that window instead.
    /// </summary>
    private bool AcceptsDeltasLocked(IndexRules rules)
    {
        lock (_gate) return Meta.Complete && string.Equals(Meta.RuleKey, rules.Key, StringComparison.Ordinal);
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

            if (changed > 0) MarkSearchDirtyLocked();
        }
        return changed;
    }

    /// <summary>Watcher-sourced change: the path is known, the IDs come from the file itself.</summary>
    public int ApplyPathChanges(IReadOnlyList<PathChange> changes, IndexRules rules)
    {
        if (!AcceptsDeltasLocked(rules)) return 0;

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
                        var parentId = ParentIdForPathLocked(parentPath);
                        if (parentId == 0)
                        {
                            // Moved somewhere the index does not reach: drop it instead of
                            // reparenting it onto id 0, which no chain can ever resolve again.
                            changed += RemoveSubtreeLocked(id);
                            continue;
                        }
                        ReparentLocked(existing, parentId, name, change.Path);
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
                var newParentId = ParentIdForPathLocked(folder);
                // Without an indexed parent this row would carry a path nothing can rebuild: it
                // would answer one search and be dropped as an orphan on the next restart.
                if (newParentId == 0) continue;

                var entry = MakeEntry(id, newParentId, change.Path, name, isDirectory, FileTime(ticks));
                AddLocked(entry);
                changed++;
                if (isDirectory) changed += PopulateSubtreeLocked(entry, rules);
            }

            if (changed > 0) MarkSearchDirtyLocked();
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
        CountExtensionLocked(entry, +1);
        // Appending keeps the flat search list usable without the O(n) rebuild; a removal is the
        // only thing that has to start over.
        if (!_searchListDirty) _searchList.Add(entry);
        AppendToNarrowSetLocked(entry);
        // One list per *parent*, not per row: this is what makes a folder rename update its
        // whole subtree without scanning the index.
        if (!_childIds.TryGetValue(entry.ParentId, out var siblings))
        {
            siblings = new List<ulong>();
            _childIds[entry.ParentId] = siblings;
        }
        // Callers guarantee a fresh id (the journal checks `_byId` first), so no O(siblings) scan.
        siblings.Add(entry.Id);
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
            CountExtensionLocked(node, -1);
            _byPath.Remove(node.Path);
            if (_childIds.TryGetValue(current, out var children))
            {
                foreach (var child in children) pending.Enqueue(child);
                _childIds.Remove(current);
            }
            if (_childIds.TryGetValue(node.ParentId, out var siblings)) siblings.Remove(current);
            removed++;
        }

        MarkSearchDirtyLocked();
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
            CountExtensionLocked(entry, -1);
            CountExtensionLocked(replacement, +1);
            _byId[entry.Id] = replacement;
            _byPath[newPath] = replacement;
            CascadePathsLocked(replacement);
        }
        else
        {
            _byPath[newPath] = entry;
            if (!string.Equals(entry.Path, newPath, StringComparison.OrdinalIgnoreCase))
            {
                WithPath(entry, newPath);
                CascadePathsLocked(entry);
            }
        }

        MarkSearchDirtyLocked();
    }

    private void CascadePathsLocked(FileEntry root)
    {
        if (!_childIds.TryGetValue(root.Id, out var children)) return;
        var pending = new Queue<FileEntry>();
        // A parent chain that loops (corrupt row, odd hardlink) would otherwise spin forever here.
        var seen = new HashSet<ulong>();
        foreach (var childId in children.ToArray())
            if (_byId.TryGetValue(childId, out var child) && seen.Add(childId)) pending.Enqueue(child);

        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            var parentPath = _byId.TryGetValue(node.ParentId, out var parent) ? parent.Path : node.Folder;
            var newPath = Path.Combine(parentPath, node.Name);
            if (!string.Equals(node.Path, newPath, StringComparison.OrdinalIgnoreCase))
            {
                _byPath.Remove(node.Path);
                WithPath(node, newPath);
                _byPath[newPath] = node;
            }
            if (_childIds.TryGetValue(node.Id, out var grandChildren))
                foreach (var grandChildId in grandChildren.ToArray())
                    if (seen.Add(grandChildId) && _byId.TryGetValue(grandChildId, out var grandChild))
                        pending.Enqueue(grandChild);
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
        _activeRuleKey = rules.Key;
        ResolvePathsLocked();
        MarkSearchDirtyLocked();
        RebuildStatsLocked();
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
                WithPath(node, Path.Combine(basePath, node.Name));
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
        MarkSearchDirtyLocked();
    }

    private ulong _syntheticId = ulong.MaxValue;
    private ulong NextSyntheticId() => _syntheticId--;

    // Root seeds get their own id range: an app row (above) and a disambiguated root seed must
    // never be able to collide, or a root's children resolve against an app's row.
    private ulong _seedId = ulong.MaxValue - 0x100000;
    private ulong NextSeedId() => _seedId--;

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
            MarkSearchDirtyLocked();
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
                MarkSearchDirtyLocked();
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

            // Structure version sits right after the magic. A file written before this existed
            // has the JSON length here instead, so the comparison fails and the index is rebuilt —
            // which is exactly what we want for the first run of a release that changed the rows.
            var storedVersion = reader.ReadInt32();
            if (storedVersion != Infrastructure.BuildInfo.IndexVersion)
            {
                LastLoadError = $"structure {storedVersion} != {Infrastructure.BuildInfo.IndexVersion}";
                return false;
            }

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
                    PathSearch = string.Empty,
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
            // The rows must belong to the rule set we were last asked to honour. Persisting a
            // build that lost the race against a config change is how an index ends up on disk
            // that the very next startup has to throw away — and how the watched deltas that
            // arrived in between become parentless ghosts.
            if (!Meta.Complete || _byId.Count == 0) return;
            if (_activeRuleKey is { } active && !string.Equals(Meta.RuleKey, active, StringComparison.Ordinal)) return;
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
                writer.Write(Infrastructure.BuildInfo.IndexVersion);
                meta.IndexVersion = Infrastructure.BuildInfo.IndexVersion;
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
            lock (_gate)
            {
                _dirty = false;
                _dirtyBatches = 0;
            }
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
            _staleStamp++;
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
