using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

/// <summary>
/// Resolves the exact shell icon Explorer shows for a path. Uses SHGetFileInfo to pull the
/// HICON straight out of the shell's system image list (the same cache Explorer renders from),
/// so the tile icon is pixel-identical to the one next to the file in Explorer.
/// Extraction runs on an STA worker thread; results are frozen and cached by path.
/// Store-app logos come from the package manifest, and targets that no longer exist are
/// flagged so the tile can show a warning instead of pretending everything is fine.
/// </summary>
public static class IconService
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SHELLICONSIZE = 0x4; // use the DPI-aware shell icon size
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;
    private const int ShellIconPixels = 256;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static Task LoadAsync(IEnumerable<ShortcutItem> items)
    {
        var all = items.ToList();
        var pending = all.Where(i => i.Icon is null && i.Kind != ShortcutKind.Url).ToList();
        if (pending.Count == 0 && all.All(i => !i.NeedsAvailabilityProbe)) return Task.CompletedTask;

        var dispatcher = Application.Current.Dispatcher;
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var item in all)
                {
                    if (!item.NeedsAvailabilityProbe) continue;
                    var missing = !File.Exists(item.Target) && !Directory.Exists(item.Target);
                    var target = item;
                    dispatcher.BeginInvoke(() => target.IsUnavailable = missing);
                }

                // Store apps: one package enumeration for all tiles that still need a logo.
                var appx = pending.Where(i => i.Kind == ShortcutKind.AppX).ToList();
                if (appx.Count > 0)
                {
                    var logos = StoreAppsService.GetLogos(appx.Select(i => i.Target), 256);
                    dispatcher.BeginInvoke(() =>
                    {
                        foreach (var app in appx)
                            if (logos.TryGetValue(app.Target, out var logo)) app.Icon = logo;
                    });
                }

                foreach (var item in pending.Where(i => i.Kind != ShortcutKind.AppX))
                {
                    var icon = Get(item.Target);
                    if (icon is null) continue;
                    var target = item;
                    dispatcher.BeginInvoke(() => target.Icon = icon);
                }
            }
            catch
            {
                // A missing icon must never take the app down.
            }
            finally
            {
                tcs.TrySetResult();
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static ImageSource? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }

        ImageSource? result = null;
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                result = FromShellImage(path) ?? FromShellIcon(path);
        }
        catch { result = null; }

        lock (Gate) { Cache[path] = result; }
        return result;
    }

    /// <summary>
    /// Asks the shell for the item's real 256 px icon. SHGetFileInfo only exposes the
    /// 32 px "large" icon, which is exactly why tiles used to look blurry at 40-48 px.
    /// </summary>
    private static ImageSource? FromShellImage(string path)
    {
        var iid = typeof(Native.IShellItemImageFactory).GUID;
        if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            return null;

        try
        {
            var hr = factory.GetImage(
                new Native.SIZE { cx = ShellIconPixels, cy = ShellIconPixels },
                SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK,
                out var hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                Native.DeleteObject(hBitmap);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Gets the icon Explorer shows: the shell's own HICON, alpha intact, no shortcuts.</summary>
    private static ImageSource? FromShellIcon(string path)
    {
        var info = new Native.SHFILEINFO();
        var res = Native.SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON | SHGFI_SHELLICONSIZE);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { Native.DestroyIcon(info.hIcon); }
    }
}
