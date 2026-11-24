using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Management.Deployment;

namespace DesktopJumpList.Services;

/// <summary>
/// Enumerates installed Microsoft Store (UWP/packaged) apps so they can be added to Jump.
/// Each app is identified by its AppUserModelId (AUMID), launched via shell:AppsFolder.
/// </summary>
public static class StoreAppsService
{
    public sealed record StoreApp(string Name, string AppUserModelId);

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

    /// <summary>Gets a store app's logo as a frozen BitmapSource, or null to fall back to a letter tile.</summary>
    public static ImageSource? GetLogo(string appUserModelId, int size)
    {
        try
        {
            var pm = new PackageManager();
            foreach (var pkg in pm.FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main))
            {
                if (pkg.IsFramework || pkg.IsResourcePackage) continue;
                foreach (var entry in pkg.GetAppListEntries())
                {
                    if (!string.Equals(entry.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase)) continue;

                    var streamRef = entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(size, size));
                    if (streamRef is null) return null;
                    using var stream = streamRef.OpenReadAsync().AsTask().GetAwaiter().GetResult();
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream.AsStreamForRead();
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
        }
        catch { }
        return null;
    }
}
