using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

/// <summary>
/// Resolves real shell icons (the same ones Explorer shows). Extraction runs off the UI thread
/// on an STA worker and results are frozen + cached by target path.
/// </summary>
public static class IconService
{
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
            {
                result = FromImageFactory(path, 64) ?? FromFileInfo(path);
            }
        }
        catch { result = null; }

        lock (Gate) { Cache[path] = result; }
        return result;
    }

    private static ImageSource? FromImageFactory(string path, int size)
    {
        var iid = typeof(Native.IShellItemImageFactory).GUID;
        if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            return null;
        try
        {
            // SIIGBF_ICONONLY (0x4): we want the app/file icon, not a document thumbnail.
            if (factory.GetImage(new Native.SIZE { cx = size, cy = size }, 0x4, out var hbmp) != 0 || hbmp == IntPtr.Zero)
                return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var fixedAlpha = FixPremultiplied(src);
                fixedAlpha.Freeze();
                return fixedAlpha;
            }
            finally { Native.DeleteObject(hbmp); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    /// <summary>
    /// HBITMAPs from the shell carry a real alpha channel, but CreateBitmapSourceFromHBitmap
    /// labels them Bgr32/Pbgra32 inconsistently. Re-wrap the pixels as Bgra32 so edges stay smooth.
    /// </summary>
    private static BitmapSource FixPremultiplied(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var pixels = new byte[h * stride];
        src.CopyPixels(pixels, stride, 0);

        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        if (!hasAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
    }

    private static ImageSource? FromFileInfo(string path)
    {
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;
        var info = new Native.SHFILEINFO();
        var res = Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_LARGEICON);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { Native.DestroyIcon(info.hIcon); }
    }
}
