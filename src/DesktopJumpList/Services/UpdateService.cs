using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DesktopJumpList.Services;

/// <summary>
/// Update check against the public GitHub Releases of the project. No auth: the repo API is
/// readable anonymously; the installer asset is whatever "-setup.exe" the latest release ships.
/// </summary>
public static class UpdateService
{
    public const string RepoUrl = "https://github.com/1182793425/Desktop-Jump-List";
    public const string ReleasesUrl = RepoUrl + "/releases";

    private const string LatestApi = "https://api.github.com/repos/1182793425/Desktop-Jump-List/releases/latest";
    private const string ReleasesApi = "https://api.github.com/repos/1182793425/Desktop-Jump-List/releases";

    public sealed record ReleaseInfo(string Tag, Version Version, string? AssetUrl, string? AssetFileName);

    /// <summary>One release's download stats: tag name, version and total downloads of its setup assets.</summary>
    public sealed record DownloadStat(string Tag, Version? Version, int Downloads);

    /// <summary>Download counts for every published release, oldest first. Null version when the tag is not semver.</summary>
    public static async Task<List<DownloadStat>> GetDownloadStatsAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopJumpList-UpdateCheck");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var json = await http.GetStringAsync(ReleasesApi + "?per_page=100", ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<DownloadStat>();
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.GetProperty("draft").GetBoolean()) continue;
            var tag = rel.GetProperty("tag_name").GetString() ?? string.Empty;
            var version = Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;
            var downloads = 0;
            if (rel.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? string.Empty;
                    if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                        downloads += asset.GetProperty("download_count").GetInt32();
                }
            }
            list.Add(new DownloadStat(tag, version, downloads));
        }

        list.Sort((a, b) => (a.Version ?? new Version()).CompareTo(b.Version ?? new Version()));
        return list;
    }

    /// <summary>Returns the latest published release, or null when the payload makes no sense.</summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopJumpList-UpdateCheck");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var json = await http.GetStringAsync(LatestApi, ct);
        using var doc = JsonDocument.Parse(json);

        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        var version = Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? v : null;
        if (version is null) return null;

        string? assetUrl = null;
        string? assetName = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    assetName = name;
                    break;
                }
            }
        }

        return new ReleaseInfo(tag, version, assetUrl, assetName);
    }

    /// <summary>Streams the installer to <paramref name="dest"/> (via a .download temp file),
    /// reporting 0-100 progress when the total size is known.</summary>
    public static async Task DownloadAsync(string url, string dest, Action<int>? progress, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopJumpList-UpdateCheck");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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
}
