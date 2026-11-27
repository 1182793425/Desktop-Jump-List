using System.IO;
using System.Text.Json;

namespace DesktopJumpList.Services;

/// <summary>
/// How often (and how recently) the user opened a file from the search box. The index itself has
/// no idea what "mine" means — it only knows last-write time — so this is the one memory that
/// makes a frequently used file float above its neighbours in a tie.
/// </summary>
/// <remarks>
/// Reads are lock-free: every write publishes a fresh copy of the map, so a search over 100k
/// entries pays one dictionary probe per hit and never blocks the thread that records an open.
/// Persisted as a small JSON beside the index; capped, because only the head of the list matters.
/// </remarks>
public sealed class UsageStore
{
    /// <summary>Paths kept on disk — enough for years of real use, small enough to load eagerly.</summary>
    private const int MaxEntries = 600;

    /// <summary>An open older than this stops counting as "recent".</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);

    private readonly string? _path;
    private readonly object _writeGate = new();
    private readonly System.Windows.Threading.DispatcherTimer? _saveTimer;

    private volatile Dictionary<string, Counters> _map;

    public UsageStore(string? directory = null)
    {
        _path = directory is null ? null : Path.Combine(directory, "usage.json");
        _map = Load(_path);

        if (_path is null) return;
        _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _saveTimer.Tick += (_, _) => { _saveTimer!.Stop(); Save(); };
    }

    public int Count => _map.Count;

    /// <summary>
    /// Ranking bonus, 0..3: two steps for repeat use and one for a recent open. Deliberately
    /// small — it settles ties, it never promotes a vague match over an exact one.
    /// </summary>
    /// <remarks>
    /// A single open earns nothing. One accidental launch used to pin that file to the top of
    /// every query it appeared in for two weeks ("那两个 png 永远排最前面"), which is not what a
    /// preference is supposed to feel like.
    /// </remarks>
    public int Boost(string path)
    {
        if (path.Length == 0) return 0;
        var map = _map;
        if (map.Count == 0) return 0;
        if (!map.TryGetValue(path, out var record))
        {
            // The index stores '\' paths; a caller may still hand us a pasted '/' one.
            if (!map.TryGetValue(path.Replace('/', '\\'), out record)) return 0;
        }
        if (record.Count < 2) return 0;
        return Math.Min(2, record.Count) + (IsRecent(record) ? 1 : 0);
    }

    /// <summary>Records one open. Called on the UI thread right after the file launched.</summary>
    public void Record(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // One key per file: a pasted "D:/x/y.pdf" and the index's "D:\x\y.pdf" are the same file.
        var key = path.Replace('/', '\\');

        lock (_writeGate)
        {
            var next = new Dictionary<string, Counters>(_map, StringComparer.OrdinalIgnoreCase);
            next.TryGetValue(key, out var record);
            next[key] = new Counters
            {
                Count = Math.Min(999, (record?.Count ?? 0) + 1),
                Last = DateTime.UtcNow
            };

            if (next.Count > MaxEntries)
            {
                // Keep the head of the list: most used, then most recent.
                foreach (var stale in next
                             .OrderByDescending(pair => Math.Min(2, pair.Value.Count))
                             .ThenByDescending(pair => pair.Value.Last)
                             .Skip(MaxEntries)
                             .Select(pair => pair.Key)
                             .ToList())
                    next.Remove(stale);
            }

            _map = next;
        }

        if (_saveTimer is { } timer)
        {
            timer.Stop();
            timer.Start();
        }
    }

    /// <summary>Drops paths that no longer exist, so a renamed file does not keep its ghost score.</summary>
    public void Prune()
    {
        var seen = _map;
        if (seen.Count == 0) return;

        Dictionary<string, Counters>? next = null;
        foreach (var (path, _) in seen)
        {
            if (File.Exists(path) || Directory.Exists(path)) continue;
            next ??= new Dictionary<string, Counters>(seen, StringComparer.OrdinalIgnoreCase);
            next.Remove(path);
        }

        if (next is null) return;
        lock (_writeGate) _map = next;
        Save();
    }

    public void Save()
    {
        if (_path is null) return;
        try
        {
            var payload = _map.ToDictionary(pair => pair.Key, pair => pair.Value);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload));
            File.Move(temp, _path, overwrite: true);
        }
        catch { /* a lost history file must never break a launch */ }
    }

    private static bool IsRecent(Counters record) =>
        record.Last != default && DateTime.UtcNow - record.Last < RecentWindow;

    private static Dictionary<string, Counters> Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new Dictionary<string, Counters>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Counters>>(File.ReadAllText(path));
            return loaded is null
                ? new Dictionary<string, Counters>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Counters>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, Counters>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class Counters
    {
        public int Count { get; set; }
        public DateTime Last { get; set; }
    }
}
