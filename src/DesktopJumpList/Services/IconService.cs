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
/// </summary>
public static class IconService
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SHELLICONSIZE = 0x4; // use the DPI-aware shell icon size

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static Task LoadAsync(IEnumerable<ShortcutItem> items)
    {
        var pending = items.Where(i => i.Icon is null && i.Kind != ShortcutKind.Url).ToList();
        if (pending.Count == 0) return Task.CompletedTask;

        var dispatcher = Application.Current.Dispatcher;
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            foreach (var item in pending)
            {
                var icon = Get(item.Target);
                if (icon is not null)
                {
                    dispatcher.BeginInvoke(() => item.Icon = icon);
                }
            }
            tcs.TrySetResult();
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
                result = FromShellIcon(path);
        }
        catch { result = null; }

        lock (Gate) { Cache[path] = result; }
        return result;
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
