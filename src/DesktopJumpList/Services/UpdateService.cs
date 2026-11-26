using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace DesktopJumpList.Services;

/// <summary>
/// Update check against the public GitHub Releases of the project. No auth: the repo API is
/// readable anonymously — but rate-limited to 60 requests/hour per IP, so every fetch goes
/// through one shared HttpClient and one /releases call that serves both the update check and
/// the download-stats chart, with a disk cache as fallback when the quota is exhausted.
/// </summary>
public static class UpdateService
{
    public const string RepoUrl = "https://github.com/1182793425/Desktop-Jump-List";
    public const string ReleasesUrl = RepoUrl + "/releases";

    private const string ReleasesApi = "https://api.github.com/repos/1182793425/Desktop-Jump-List/releases";

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

    /// <summary>The result of one successful (or cached) fetch of the releases list.</summary>
    public sealed record ReleaseSnapshot(List<ReleaseEntry> Releases, DateTime FetchedAtUtc, bool FromCache);

    /// <summary>One release's download stats: tag name, version and total downloads of its setup assets.</summary>
    public sealed record DownloadStat(string Tag, Version? Version, int Downloads);

    private static string CachePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopJumpList");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "releases-cache.json");
        }
    }

    /// <summary>
    /// Fetches all published releases. Network first; on failure (rate limit 403, offline, …)
    /// falls back to the last successful copy on disk so the UI still shows something, marked
    /// <see cref="ReleaseSnapshot.FromCache"/>. Returns null only with no network and no cache.
    /// </summary>
    public static async Task<ReleaseSnapshot?> GetReleasesAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(ReleasesApi + "?per_page=100", ct);
            var releases = Parse(json);
            if (releases.Count == 0) return null;
            var snapshot = new ReleaseSnapshot(releases, DateTime.UtcNow, FromCache: false);
            WriteCache(snapshot);
            return snapshot;
        }
        catch (Exception)
        {
            return ReadCache();
        }
    }

    /// <summary>True when the exception is GitHub's anonymous rate limit (403).</summary>
    public static bool IsRateLimit(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden };

    private static List<ReleaseEntry> Parse(string json)
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

    private static void WriteCache(ReleaseSnapshot snapshot)
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
            File.WriteAllText(CachePath, JsonSerializer.Serialize(payload));
        }
        catch { /* cache is best-effort */ }
    }

    private static ReleaseSnapshot? ReadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
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

    /// <summary>Returns the latest published release, or null when nothing sensible is known.</summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        var snapshot = await GetReleasesAsync(ct);
        if (snapshot is null) return null;

        ReleaseEntry? latest = null;
        foreach (var r in snapshot.Releases)
            if (r.Version is not null && (latest is null || r.Version > latest.Version!))
                latest = r;
        if (latest is null) return null;

        return new ReleaseInfo(latest.Tag, latest.Version!, latest.AssetUrl, latest.AssetFileName);
    }

    /// <summary>Download counts for every published release, oldest first.</summary>
    public static async Task<List<DownloadStat>> GetDownloadStatsAsync(CancellationToken ct = default)
    {
        var snapshot = await GetReleasesAsync(ct);
        var stats = new List<DownloadStat>();
        if (snapshot is null) return stats;

        foreach (var r in snapshot.Releases)
            stats.Add(new DownloadStat(r.Tag, r.Version, r.Downloads));
        stats.Sort((a, b) => (a.Version ?? new Version()).CompareTo(b.Version ?? new Version()));
        return stats;
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
    /// <summary>Cache notice suffix: "（缓存数据，获取于 5 分钟前）" or empty.</summary>
    public static string CacheNotice(Services.UpdateService.ReleaseSnapshot snapshot) =>
        snapshot.FromCache ? $"（缓存数据，获取于 {Services.UpdateService.AgeText(snapshot.FetchedAtUtc)}；GitHub 匿名限额 60 次/小时，用完需等待）" : string.Empty;
}
