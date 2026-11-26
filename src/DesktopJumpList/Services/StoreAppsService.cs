using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Management.Deployment;

namespace DesktopJumpList.Services;

/// <summary>
/// Enumerates installed Microsoft Store (UWP/packaged) apps so they can be added to Jump.
/// Each app is identified by its AppUserModelId (AUMID), launched via shell:AppsFolder.
/// Logo lookups are cached per AUMID so tiles keep their real icon across restarts.
/// </summary>
public static class StoreAppsService
{
    public sealed record StoreApp(string Name, string AppUserModelId);

    private static readonly Dictionary<string, ImageSource?> LogoCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>Returns all launchable store apps, sorted by name. Safe to call on a worker thread.</summary>
    public static List<StoreApp> Enumerate()
    {
        var apps = new List<StoreApp>();
        try
        {
            var pm = new PackageManager();
            foreach (var pkg in pm.FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main))
            {
                if (pkg.IsFramework || pkg.IsResourcePackage) continue;
                try
                {
                    foreach (var entry in pkg.GetAppListEntries())
                    {
                        var name = entry.DisplayInfo.DisplayName;
                        var aumid = entry.AppUserModelId;
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(aumid))
                            apps.Add(new StoreApp(name, aumid));
                    }
                }
                catch { /* skip broken packages */ }
            }
        }
        catch { }
        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Gets a single app logo. Cached, so repeated opens are instant.</summary>
    public static ImageSource? GetLogo(string appUserModelId, int size)
    {
        lock (Gate)
        {
            if (LogoCache.TryGetValue(appUserModelId, out var cached)) return cached;
        }
        GetLogos(new[] { appUserModelId }, size);
        lock (Gate)
        {
            return LogoCache.TryGetValue(appUserModelId, out var loaded) ? loaded : null;
        }
    }

    /// <summary>
    /// Resolves several logos with one package enumeration. Missing or broken packages are
    /// cached as null so startup does not rescan for them on every launch.
    /// </summary>
    public static Dictionary<string, ImageSource> GetLogos(IEnumerable<string> appUserModelIds, int size)
    {
        var wanted = new HashSet<string>(
            appUserModelIds.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;

        lock (Gate)
        {
            foreach (var id in wanted)
                if (LogoCache.TryGetValue(id, out var cached) && cached is not null) result[id] = cached;
        }

        var remaining = wanted.Where(id => !result.ContainsKey(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (remaining.Count == 0) return result;

        try
        {
            var pm = new PackageManager();
            foreach (var pkg in pm.FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main))
            {
                if (pkg.IsFramework || pkg.IsResourcePackage) continue;
                try
                {
                    foreach (var entry in pkg.GetAppListEntries())
                    {
                        var aumid = entry.AppUserModelId;
                        if (!remaining.Contains(aumid)) continue;

                        // Ask the package for its large artwork. The common 44px request used
                        // to select a compact logo variant, which looked soft on scaled displays.
                        var logoSize = Math.Max(size, 256);
                        var streamRef = entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(logoSize, logoSize));
                        if (streamRef is null) continue;
                        using var stream = streamRef.OpenReadAsync().AsTask().GetAwaiter().GetResult();
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream.AsStreamForRead();
                        bitmap.EndInit();
                        bitmap.Freeze();

                        result[aumid] = bitmap;
                        remaining.Remove(aumid);
                    }
                }
                catch { /* skip broken packages */ }
            }
        }
        catch { }

        lock (Gate)
        {
            foreach (var id in wanted)
            {
                if (result.TryGetValue(id, out var logo)) LogoCache[id] = logo;
                else if (!LogoCache.ContainsKey(id)) LogoCache[id] = null;
            }
        }
        return result;
    }
}
