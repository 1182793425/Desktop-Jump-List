using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DesktopJumpList.Services;

/// <summary>
/// Update check and download stats against the public GitHub Releases of the project. No auth:
/// the repo API is readable anonymously but rate-limited (60 requests/hour per IP), so the two
/// features stay separate — the update check uses the light /releases/latest endpoint, the
/// download stats use one page of /releases and are locally throttled to one network fetch per
/// hour per device, with each result cached to disk and used as fallback.
/// </summary>
public static class UpdateService
{
    public const string RepoUrl = "https://github.com/1182793425/Desktop-Jump-List";
    public const string ReleasesUrl = RepoUrl + "/releases";

    private const string LatestApi = "https://api.github.com/repos/1182793425/Desktop-Jump-List/releases/latest";
    private const string ReleasesApi = "https://api.github.com/repos/1182793425/Desktop-Jump-List/releases";

    /// <summary>Local, per-device throttle for the download-stats network fetch.</summary>
    private static readonly TimeSpan StatsLocalInterval = TimeSpan.FromHours(1);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopJumpList-UpdateCheck");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public sealed record ReleaseInfo(string Tag, Version Version, string? AssetUrl, string? AssetFileName);

    /// <summary>One release: tag, version and total downloads of its setup assets.</summary>
    public sealed record ReleaseEntry(string Tag, Version? Version, int Downloads, string? AssetUrl, string? AssetFileName);

    /// <summary>
    /// The outcome of one stats/latest request. <see cref="FromCache"/> covers both a network
    /// failure fallback and a locally throttled read; <see cref="ThrottledLocally"/> is set when
    /// the network was not touched at all because the local one-hour window was still running.
    /// </summary>
    public sealed record ReleaseSnapshot(List<ReleaseEntry> Releases, DateTime FetchedAtUtc, bool FromCache, bool ThrottledLocally = false);

    /// <summary>One release's download stats: tag name, version and total downloads of its setup assets.</summary>
    public sealed record DownloadStat(string Tag, Version? Version, int Downloads);

    private static string CacheDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopJumpList");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ───────────────────────── 检查更新（独立轻量端点） ─────────────────────────

    /// <summary>
    /// Latest release via /releases/latest — one small request, entirely separate from the
    /// stats pipeline. Network first; on failure falls back to the local copy.
    /// </summary>
    public static async Task<ReleaseSnapshot?> CheckLatestAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(LatestApi, ct);
            var entry = ParseOne(json);
            if (entry is null) return ReadCache("latest-cache.json");
            var snapshot = new ReleaseSnapshot([entry], DateTime.UtcNow, FromCache: false);
            WriteCache("latest-cache.json", snapshot);
            return snapshot;
        }
        catch (Exception)
        {
            return ReadCache("latest-cache.json");
        }
    }

    /// <summary>Parses a single /releases/latest payload into an entry.</summary>
    private static ReleaseEntry? ParseOne(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var version = Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;
        if (version is null) return null;

        string? assetUrl = null;
        string? assetName = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                assetUrl = asset.GetProperty("browser_download_url").GetString();
                assetName = name;
                break;
            }
        }
        return new ReleaseEntry(tag, version, 0, assetUrl, assetName);
    }

    /// <summary>Returns the latest published release, or null when nothing sensible is known.</summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        var snapshot = await CheckLatestAsync(ct);
        if (snapshot is null) return null;

        ReleaseEntry? latest = null;
        foreach (var r in snapshot.Releases)
            if (r.Version is not null && (latest is null || r.Version > latest.Version!))
                latest = r;
        if (latest is null) return null;

        return new ReleaseInfo(latest.Tag, latest.Version!, latest.AssetUrl, latest.AssetFileName);
    }

    // ───────────────────────── 下载统计（独立 + 本地每小时一次） ─────────────────────────

    /// <summary>
    /// Download stats from the FIRST page of /releases only. Locally throttled: once the
    /// on-disk cache is younger than one hour, the network is not touched at all and the
    /// cache is returned marked <see cref="ReleaseSnapshot.ThrottledLocally"/>. On network
    /// failure the cache is still the fallback. Returns null with no network and no cache.
    /// </summary>
    public static async Task<ReleaseSnapshot?> GetDownloadStatsAsync(CancellationToken ct = default)
    {
        var cached = ReadCache("stats-cache.json");
        if (cached is not null && DateTime.UtcNow - cached.FetchedAtUtc < StatsLocalInterval)
            return cached with { ThrottledLocally = true };

        try
        {
            // First page only: 100 releases is far more than this project will ever need,
            // and every extra page burns anonymous quota for nothing.
            var json = await Http.GetStringAsync(ReleasesApi + "?per_page=100&page=1", ct);
            var releases = ParseReleases(json);
            if (releases.Count == 0) return cached;

            var snapshot = new ReleaseSnapshot(releases, DateTime.UtcNow, FromCache: false);
            WriteCache("stats-cache.json", snapshot);
            return snapshot;
        }
        catch (Exception)
        {
            return cached;
        }
    }

    /// <summary>Download counts for every release on the latest page, oldest first.</summary>
    public static async Task<List<DownloadStat>> GetDownloadStatsListAsync(CancellationToken ct = default)
    {
        var snapshot = await GetDownloadStatsAsync(ct);
        var stats = new List<DownloadStat>();
        if (snapshot is null) return stats;

        foreach (var r in snapshot.Releases)
            stats.Add(new DownloadStat(r.Tag, r.Version, r.Downloads));
        stats.Sort((a, b) => (a.Version ?? new Version()).CompareTo(b.Version ?? new Version()));
        return stats;
    }

    private static List<ReleaseEntry> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<ReleaseEntry>();
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.GetProperty("draft").GetBoolean()) continue;
            var tag = rel.GetProperty("tag_name").GetString() ?? string.Empty;
            var version = Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;

            string? assetUrl = null;
            string? assetName = null;
            var downloads = 0;
            if (rel.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    downloads += asset.GetProperty("download_count").GetInt32();
                    assetUrl ??= asset.GetProperty("browser_download_url").GetString();
                    assetName ??= name;
                }
            }
            list.Add(new ReleaseEntry(tag, version, downloads, assetUrl, assetName));
        }
        return list;
    }

    // ───────────────────────── 缓存读写 ─────────────────────────

    private static void WriteCache(string fileName, ReleaseSnapshot snapshot)
    {
        try
        {
            var payload = new
            {
                fetchedAtUtc = snapshot.FetchedAtUtc,
                releases = snapshot.Releases.Select(r => new
                {
                    r.Tag, r.Version, r.Downloads, r.AssetUrl, r.AssetFileName
                })
            };
            File.WriteAllText(Path.Combine(CacheDir, fileName), JsonSerializer.Serialize(payload));
        }
        catch { /* cache is best-effort */ }
    }

    private static ReleaseSnapshot? ReadCache(string fileName)
    {
        try
        {
            var path = Path.Combine(CacheDir, fileName);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var fetchedAt = doc.RootElement.GetProperty("fetchedAtUtc").GetDateTime();
            var list = new List<ReleaseEntry>();
            foreach (var r in doc.RootElement.GetProperty("releases").EnumerateArray())
            {
                var tag = r.GetProperty("Tag").GetString() ?? string.Empty;
                Version? version = r.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String
                    ? Version.TryParse(v.GetString(), out var ver) ? ver : null : null;
                var downloads = r.TryGetProperty("Downloads", out var d) ? d.GetInt32() : 0;
                string? assetUrl = r.TryGetProperty("AssetUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                string? assetName = r.TryGetProperty("AssetFileName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                list.Add(new ReleaseEntry(tag, version, downloads, assetUrl, assetName));
            }
            return list.Count == 0 ? null : new ReleaseSnapshot(list, fetchedAt, FromCache: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Minutes until the download-stats throttle expires (for UI notices).</summary>
    public static int MinutesUntilStatsRefresh(ReleaseSnapshot snapshot)
    {
        var remaining = StatsLocalInterval - (DateTime.UtcNow - snapshot.FetchedAtUtc);
        return remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalMinutes);
    }

    /// <summary>Streams the installer to <paramref name="dest"/> (via a .download temp file),
    /// reporting 0-100 progress when the total size is known. Downloads hit github.com, not the
    /// rate-limited API, so they are unaffected by the anonymous quota.</summary>
    public static async Task DownloadAsync(string url, string dest, Action<int>? progress, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        var temp = dest + ".download";
        await using (var source = await resp.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Invoke((int)(read * 100 / total));
            }
        }

        File.Move(temp, dest, overwrite: true);
    }

    /// <summary>"5 分钟前" style relative time for cache-age notices.</summary>
    public static string AgeText(DateTime fetchedAtUtc)
    {
        var age = DateTime.UtcNow - fetchedAtUtc;
        if (age.TotalMinutes < 1) return "刚刚";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} 分钟前";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} 小时前";
        return $"{(int)age.TotalDays} 天前";
    }
}

/// <summary>Release snapshot plus how it was obtained, for UI notices.</summary>
public static class UpdateServiceExtensions
{
    /// <summary>Cache notice suffix for the update check.</summary>
    public static string CacheNotice(Services.UpdateService.ReleaseSnapshot snapshot) =>
        snapshot.FromCache
            ? $"（本地缓存，获取于 {Services.UpdateService.AgeText(snapshot.FetchedAtUtc)}；GitHub 匿名限额 60 次/小时）"
            : string.Empty;

    /// <summary>Cache notice suffix for the download stats.</summary>
    public static string StatsNotice(Services.UpdateService.ReleaseSnapshot snapshot)
    {
        if (snapshot.ThrottledLocally)
            return $"（本地缓存，获取于 {Services.UpdateService.AgeText(snapshot.FetchedAtUtc)}；本机每小时联网刷新一次，约 {Services.UpdateService.MinutesUntilStatsRefresh(snapshot)} 分钟后可刷新）";
        if (snapshot.FromCache)
            return $"（本地缓存，获取于 {Services.UpdateService.AgeText(snapshot.FetchedAtUtc)}）";
        return string.Empty;
    }
}
