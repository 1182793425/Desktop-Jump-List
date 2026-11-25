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
/// Resolves the exact shell icon Explorer shows for a path — the same pipeline the panel has
/// always used (v3.12.0): SHGetFileInfo pulls the native HICON out of the shell's icon cache,
/// which is sharp at every DPI. Extraction runs on an STA worker thread; results are frozen
/// and cached by path.
/// Shortcuts resolve to their target first, so a .lnk tile shows the target's real icon
/// instead of the shortcut-arrow overlay.
/// </summary>
public static class IconService
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SHELLICONSIZE = 0x4; // use the DPI-aware shell icon size
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    /// <summary>
    /// Display size used when asking the shell for icons. Kept for diagnostics only — the
    /// native HICON size is chosen by the shell itself (DPI-aware), never upscaled by us.
    /// </summary>
    internal static int IconPixels
    {
        get
        {
            var size = App.Current?.Settings?.PanelIconSize ?? 48;
            var wanted = (int)Math.Ceiling((Math.Max(0, size) + 8) * Native.SystemScale);
            return Math.Clamp(wanted, 32, 128);
        }
    }

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
                    var logos = StoreAppsService.GetLogos(appx.Select(i => i.Target), IconPixels);
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

    /// <summary>
    /// Cache-only lookup for deferred rendering: results show instantly with a letter
    /// fallback, then this returns the extracted icon once a background pass has it.
    /// </summary>
    public static ImageSource? TryGetCached(string path)
    {
        lock (Gate)
        {
            return Cache.TryGetValue(path, out var cached) ? cached : null;
        }
    }

    public static ImageSource? Get(string path) => Get(path, depth: 0);

    private static ImageSource? Get(string path, int depth)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }

        ImageSource? result = null;
        try
        {
            // A shortcut shows its target's icon: resolve the .lnk and use the target path.
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && depth < 2
                && TryResolveShortcutTarget(path, out var target)
                && (File.Exists(target) || Directory.Exists(target)))
            {
                result = Get(target, depth + 1);
            }
            else if (File.Exists(path) || Directory.Exists(path))
            {
                // Exactly the v3.12.0 pipeline: the shell's own HICON, alpha intact, sharp.
                result = FromShellIcon(path);
            }
        }
        catch { result = null; }

        lock (Gate) { Cache[path] = result; }
        return result;
    }

    /// <summary>Gets the icon Explorer shows: the shell's own HICON, alpha intact, no shortcuts.</summary>
    private static ImageSource? FromShellIcon(string path)
    {
        var icon = QueryIcon(path, 0, SHGFI_ICON | SHGFI_LARGEICON | SHGFI_SHELLICONSIZE);
        if (icon != IntPtr.Zero) return FromHandle(icon);

        // Fallback for files the shell cannot map to a live item (empty files, odd paths):
        // derive the icon from the extension only, which never touches the disk.
        var attribute = Directory.Exists(path) ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        icon = QueryIcon(path, attribute, SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        return icon != IntPtr.Zero ? FromHandle(icon) : null;
    }

    private static IntPtr QueryIcon(string path, uint attributes, uint flags)
    {
        var info = new Native.SHFILEINFO();
        var res = Native.SHGetFileInfo(path, attributes, ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(), flags);
        return res == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
    }

    private static ImageSource? FromHandle(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { Native.DestroyIcon(icon); }
    }

    /// <summary>
    /// Resolves a .lnk to its target path by parsing the shell-link binary structure
    /// directly (MS-SHLLINK LinkInfo). No COM involved — deterministic on every machine.
    /// </summary>
    private static bool TryResolveShortcutTarget(string lnkPath, out string target)
    {
        target = string.Empty;
        try
        {
            var bytes = File.ReadAllBytes(lnkPath);
            if (bytes.Length < 0x4C) return false;
            var headerSize = BitConverter.ToInt32(bytes, 0);
            if (headerSize < 0x4C || headerSize + 4 > bytes.Length) return false;
            var flags = BitConverter.ToInt32(bytes, 0x14);

            var pos = headerSize;
            if ((flags & 0x1) != 0) // HasTargetIDList: skip the item-id list
            {
                if (pos + 2 > bytes.Length) return false;
                var idListSize = BitConverter.ToUInt16(bytes, pos);
                pos += 2 + idListSize;
                if (pos >= bytes.Length) return false;
            }

            if ((flags & 0x2) == 0) return false; // HasLinkInfo: no local target to resolve
            var linkInfoSize = BitConverter.ToInt32(bytes, pos);
            if (linkInfoSize <= 0 || pos + linkInfoSize > bytes.Length) return false;
            var linkInfoHeaderSize = BitConverter.ToInt32(bytes, pos + 4);
            var linkInfoFlags = BitConverter.ToInt32(bytes, pos + 8);
            if ((linkInfoFlags & 0x1) == 0) return false; // volume id + local base path absent

            // LinkInfo layout (MS-SHLLINK 2.3): size@+0, headerSize@+4, flags@+8,
            // VolumeIDOffset@+12, LocalBasePathOffset@+16, CommonNetworkRelativeLinkOffset@+20,
            // CommonPathSuffixOffset@+24, and — when the header is >= 0x24 —
            // LocalBasePathOffsetUnicode@+28. All offsets are relative to LinkInfo start.
            string? unicode = null;
            if (linkInfoHeaderSize >= 0x24)
            {
                var unicodeOffset = BitConverter.ToInt32(bytes, pos + 28);
                if (unicodeOffset > 0 && pos + unicodeOffset < bytes.Length)
                {
                    unicode = ReadNullTerminated(bytes, pos + unicodeOffset, utf16: true);
                }
            }

            var ansiOffset = BitConverter.ToInt32(bytes, pos + 16);
            var ansi = ansiOffset > 0 && pos + ansiOffset < bytes.Length
                ? ReadNullTerminated(bytes, pos + ansiOffset, utf16: false)
                : string.Empty;

            target = string.IsNullOrWhiteSpace(unicode) ? ansi : unicode;
            return !string.IsNullOrWhiteSpace(target);
        }
        catch { return false; }
    }

    private static string ReadNullTerminated(byte[] bytes, int start, bool utf16)
    {
        if (utf16)
        {
            var chars = new List<char>();
            var i = start;
            while (i + 1 < bytes.Length)
            {
                var c = (char)(bytes[i] | (bytes[i + 1] << 8));
                if (c == '\0') break;
                chars.Add(c);
                i += 2;
            }
            return new string(chars.ToArray());
        }

        var end = start;
        while (end < bytes.Length && bytes[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(bytes, start, end - start);
    }
}
