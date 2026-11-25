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
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    /// <summary>
    /// Pixel size to ask the shell for. Tiles top out at a few dozen DIPs, so a 256 px bitmap per
    /// item only ever cost memory (a frozen 256² surface is 256 KB); asking for just over the
    /// displayed size keeps the tiles sharp and the cache ~4-8x smaller.
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
            // shell:AppsFolder\AUMID entries (from the /s index) have no file behind them, so
            // skip the existence probe for them — the image factory resolves the item directly.
            var isShellItem = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
            // A shortcut's own icon is the target's icon: resolve it and run the full
            // pipeline on the target (depth-guarded against .lnk chains).
            var isShortcut = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && depth < 2;
            string? resolvedTarget = null;
            if (isShortcut && TryResolveShortcutTarget(path, out var shortcutTarget)
                && (File.Exists(shortcutTarget) || Directory.Exists(shortcutTarget)))
            {
                resolvedTarget = shortcutTarget;
                result = Get(shortcutTarget, depth + 1);
            }
            else if (isShellItem || File.Exists(path) || Directory.Exists(path))
            {
                // Order matters: real high-res extraction first, then the native 32px shell
                // icon (sharp). The IShellItemImageFactory route is deliberately NOT used —
                // when it succeeds it hands back the shell's smooth-upscaled bitmap, which
                // reads blurrier than the native 32px HICON it replaced (v3.14.0-3.14.2 blur).
                result = FromHighResFallback(path)
                    ?? FromShellIcon(path);
            }
            // If the shortcut resolved but the target's own pipeline failed, prefer the
            // target's *type* icon over the .lnk icon — SHGetFileInfo draws the latter with
            // the shortcut-arrow overlay, which we never want to show.
            if (result is null && isShortcut)
            {
                result = resolvedTarget is not null
                    ? TryExtractFromAssociation(resolvedTarget, IconPixels, out var src, out _)
                        ? src
                        : FromShellIcon(resolvedTarget)
                    : FromShellIcon(path);
            }
        }
        catch { result = null; }

        lock (Gate) { Cache[path] = result; }
        return result;
    }

    /// <summary>Step-by-step trace of the shortcut resolution + icon pipeline (diagnostics).</summary>
    public static string Diagnose(string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"path={path}");
        sb.AppendLine($"endsLnk={path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)}");
        var resolved = TryResolveShortcutTarget(path, out var target);
        sb.AppendLine($"resolve ok={resolved} target='{target}' exists={(File.Exists(target) || Directory.Exists(target))}");
        var img = Get(path, 0);
        sb.AppendLine($"get -> {(img is BitmapSource b ? $"{b.PixelWidth}x{b.PixelHeight}px" : "null")}");
        sb.AppendLine($"iconPixels={IconPixels}");
        return sb.ToString();
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

    /// <summary>
    /// High-resolution extraction for the types the shell pipelines cannot serve on this
    /// machine (the image factory QI answers E_NOINTERFACE for unpackaged processes):
    /// exe/dll/ico read their own resources, documents read the registered DefaultIcon,
    /// folders read the system folder icon — all at the requested pixel size, so nothing
    /// gets upscaled from a 32px source.
    /// </summary>
    private static ImageSource? FromHighResFallback(string path)
    {
        var px = IconPixels;
        var note = string.Empty;
        if (TryExtractFromFile(path, px, out var source, out note)) return source;
        if (TryExtractFromAssociation(path, px, out source, out note)) return source;
        if (TryExtractFolderIcon(path, px, out source, out note)) return source;
        _ = note;
        return null;
    }

    /// <summary>
    /// Reads the icon straight out of a PE file's resources at the requested pixel size
    /// (PrivateExtractIcons), bypassing the shell icon cache entirely.
    /// </summary>
    private static bool TryExtractFromFile(string path, int px, out ImageSource? source, out string note)
    {
        source = null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".exe" and not ".dll" and not ".ico")
        {
            note = $"extract: not a PE ({ext})";
            return false;
        }

        var handles = new IntPtr[1];
        var extracted = PrivateExtractIconsW(path, 0, px, px, handles, null, 1, 0);
        if (extracted == 0 || handles[0] == IntPtr.Zero)
        {
            note = "extract: no icon resource";
            return false;
        }

        try
        {
            note = $"ok {px}px (extracted)";
            source = FromHandle(handles[0]);
            return source is not null;
        }
        catch
        {
            note = "extract: convert failed";
            return false;
        }
    }

    /// <summary>
    /// Documents (txt/docx/pdf…): resolve the registered DefaultIcon of their file type and
    /// extract that icon at full size — the shell only offers 32px for these.
    /// </summary>
    private static bool TryExtractFromAssociation(string path, int px, out ImageSource? source, out string note)
    {
        source = null;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || ext is ".exe" or ".dll" or ".ico")
        {
            note = "assoc: not applicable";
            return false;
        }

        try
        {
            string? iconRef = null;
            using (var extKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(ext))
            {
                var progId = extKey?.GetValue(null) as string;
                if (!string.IsNullOrEmpty(progId))
                    using (var iconKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId + @"\DefaultIcon"))
                        iconRef = iconKey?.GetValue(null) as string;
                iconRef ??= extKey?.OpenSubKey("DefaultIcon")?.GetValue(null) as string;
            }

            if (string.IsNullOrWhiteSpace(iconRef))
            {
                note = "assoc: no DefaultIcon";
                return false;
            }

            // "file,index" / "file" / "%SystemRoot%\system32\imageres.dll,-102"
            iconRef = Environment.ExpandEnvironmentVariables(iconRef.Trim());
            var index = 0;
            var comma = iconRef.LastIndexOf(',');
            if (comma > 0 && int.TryParse(iconRef[(comma + 1)..].Trim(), out var parsed))
            {
                iconRef = iconRef[..comma];
                index = Math.Abs(parsed);
            }

            var file = iconRef.Trim('"', ' ');
            if (string.IsNullOrEmpty(file) || file.StartsWith("@") || !File.Exists(file))
            {
                note = "assoc: icon file missing";
                return false;
            }

            var handles = new IntPtr[1];
            var extracted = PrivateExtractIconsW(file, index, px, px, handles, null, 1, 0);
            if (extracted == 0 || handles[0] == IntPtr.Zero)
            {
                Native.DestroyIcon(handles[0]);
                note = "assoc: extract failed";
                return false;
            }

            try
            {
                note = $"ok {px}px (assoc)";
                source = FromHandle(handles[0]);
                return source is not null;
            }
            catch
            {
                note = "assoc: convert failed";
                Native.DestroyIcon(handles[0]);
                return false;
            }
        }
        catch (Exception ex)
        {
            note = "assoc: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Folders: the system folder icon lives in imageres.dll (index 2 on Win10/11); extract
    /// it at full size instead of the 32px shell image list entry.
    /// </summary>
    private static bool TryExtractFolderIcon(string path, int px, out ImageSource? source, out string note)
    {
        source = null;
        if (!Directory.Exists(path))
        {
            note = "folder: not a directory";
            return false;
        }

        var imageres = Path.Combine(Environment.SystemDirectory, "imageres.dll");
        if (!File.Exists(imageres))
        {
            note = "folder: no imageres.dll";
            return false;
        }

        var handles = new IntPtr[1];
        var extracted = PrivateExtractIconsW(imageres, 2, px, px, handles, null, 1, 0);
        if (extracted == 0 || handles[0] == IntPtr.Zero)
        {
            Native.DestroyIcon(handles[0]);
            note = "folder: extract failed";
            return false;
        }

        try
        {
            note = $"ok {px}px (folder)";
            source = FromHandle(handles[0]);
            return source is not null;
        }
        catch
        {
            note = "folder: convert failed";
            Native.DestroyIcon(handles[0]);
            return false;
        }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[]? phicon, int[]? piconid, int nIcons, int flags);

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
}
