using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

public static class IconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    public static ImageSource? GetIcon(ShortcutItem item)
    {
        if (item.Type is ShortcutType.Url or ShortcutType.Folder) return null;
        if (!string.IsNullOrWhiteSpace(item.IconPath) && File.Exists(item.IconPath))
            return LoadImage(item.IconPath);

        var path = Environment.ExpandEnvironmentVariables(item.Target);
        if (!File.Exists(path)) return null;
        return Cache.GetOrAdd(path, LoadShellIcon);
    }

    public static void Invalidate(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        Cache.TryRemove(Environment.ExpandEnvironmentVariables(target), out _);
    }

    private static ImageSource? LoadShellIcon(string path)
    {
        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(48, 48));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static ImageSource? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = 96;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
