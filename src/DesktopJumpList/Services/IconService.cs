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
/// Resolves icons through one ordered pipeline. Prefer the Windows Shell's jumbo image list
/// (256px), then its rendered item image, then native resources and finally the shell's normal
/// icon. This uses the same per-file associations and system artwork Explorer uses, while
/// keeping alpha and retaining dependable fallbacks for unusual paths and shell versions.
/// Shortcuts are queried by their own .lnk path first, preserving any custom icon location;
/// the target is used only when the link has no usable icon. Extraction runs on an STA worker
/// thread; results are frozen and cached by path.
/// </summary>
public static class IconService
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SHELLICONSIZE = 0x4; // use the DPI-aware shell icon size
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const int SHIL_JUMBO = 4;
    private const int ILD_TRANSPARENT = 1;

    /// <summary>Keep fallback extraction large enough for any panel size and high-DPI monitor.</summary>
    internal const int IconPixels = 256;

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

    /// <summary>
    /// A shell parsing name (shell:AppsFolder\AUMID) has no file behind it, but the Shell still
    /// resolves it to a live item — that is how 计算器 / 便笺 / 画图 get their real artwork
    /// instead of a letter placeholder.
    /// </summary>
    private static bool IsShellParsingName(string path) =>
        path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    private const string AppsFolderPrefix = "shell:AppsFolder\\";

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
            if (IsShellParsingName(path))
            {
                // Store / alias apps: the package logo first (it is the 256px artwork), then the
                // Shell's jumbo image list, which also answers alias entries such as 画图.
                result = FromAppsFolderLogo(path) ?? FromShellItemJumbo(path)
                    ?? FromShellImage(path) ?? FromShellIcon(path);
            }
            // Query the shortcut itself first: Windows resolves its configured IconLocation,
            // which can differ from the target executable. We do not request overlays, so
            // the custom icon is retained without Explorer's shortcut arrow.
            else if (File.Exists(path) || Directory.Exists(path))
            {
                // A .lnk answers with its own artwork, so the first usable stage wins there; any
                // other file takes the largest one, because stages disagree wildly — the same
                // program can come back as 48px from one Shell call and 256px from another.
                result = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? FromJumboShellIcon(path) ?? FromShellImage(path) ?? FromHighResFallback(path)
                        ?? FromShellIcon(path)
                    : LargestIcon(path);
            }

            // A broken or icon-less shortcut can still use the target's icon as a fallback.
            if (result is null && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && depth < 2
                && TryResolveShortcutTarget(path, out var target)
                && (File.Exists(target) || Directory.Exists(target)))
                result = Get(target, depth + 1);
        }
        catch { result = null; }

        // Icons can arrive as a big canvas with the actual picture parked in one corner (the
        // Shell pads small artwork into the next size slot). Stretched into a 48px tile that
        // reads as "a tiny icon shrunk into the top-left"; trimming makes the ink fill the bitmap.
        if (!IsShellParsingName(path)) result = TrimTransparentFrame(result);

        // A .lnk can answer with nothing but a small shell icon even though the program it starts
        // carries a full 256px one — phpstudy_pro.lnk reports 32px while its exe has 256px.
        // Swap in the better artwork, but keep the link's own icon whenever it is already good
        // enough (a shortcut may deliberately point at a custom IconLocation).
        if (InkEdge(result) < LowQualityIconEdge && depth < 2
            && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            && TryResolveShortcutTarget(path, out var source)
            && IsIconHost(source))
        {
            var fromSource = Get(source, depth + 1);
            if (InkEdge(fromSource) > InkEdge(result)) result = fromSource;
        }

        // Keep successful results only. Shell associations and removable/virtual file providers
        // can briefly fail while their caches wake up; a null must remain retryable on the next
        // visible search instead of poisoning this path for the whole process lifetime.
        if (result is not null)
            lock (Gate) { Cache[path] = result; }
        return result;
    }

    /// <summary>Below this edge length a tile would visibly upscale the artwork.</summary>
    private const int LowQualityIconEdge = 64;

    /// <summary>
    /// The biggest usable icon this file can offer. Every stage is measured by its trimmed ink, so
    /// a 256px canvas holding a 32px picture loses to a real 48px icon; the cheap stages run first
    /// and the expensive resource extraction is skipped once the artwork is already good enough.
    /// </summary>
    private static ImageSource? LargestIcon(string path)
    {
        ImageSource? best = null;
        var bestEdge = 0;

        void Consider(ImageSource? candidate)
        {
            candidate = TrimTransparentFrame(candidate);
            var edge = InkEdge(candidate);
            if (candidate is null || edge <= bestEdge) return;
            best = candidate;
            bestEdge = edge;
        }

        Consider(FromJumboShellIcon(path));
        if (bestEdge < IconPixels / 2) Consider(FromShellImage(path));
        if (bestEdge < IconPixels / 2) Consider(FromHighResFallback(path));
        if (bestEdge == 0) Consider(FromShellIcon(path));
        return best;
    }

    /// <summary>Files whose icon is the program's own artwork, worth borrowing for a shortcut.</summary>
    private static bool IsIconHost(string path) =>
        File.Exists(path)
        && (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));

    /// <summary>Longest edge of an icon in pixels; 0 when there is no image.</summary>
    private static int InkEdge(ImageSource? image) =>
        image is BitmapSource bitmap ? Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) : 0;

    /// <summary>Packaged apps: the logo the Start menu shows, keyed by the AppUserModelId.</summary>
    private static ImageSource? FromAppsFolderLogo(string path)
    {
        if (!path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var aumid = path[AppsFolderPrefix.Length..];
        // Package AUMIDs look like "Name_publisherid!AppId"; aliases do not.
        if (!aumid.Contains('!')) return null;
        return StoreAppsService.GetLogo(aumid, IconPixels);
    }

    /// <summary>
    /// 256px artwork for a shell item that has no file path: parse to a PIDL, ask for its index in
    /// the system image list, then take the jumbo image — the same route as a real exe. Without
    /// this, shell:AppsFolder rows only had the 32px shell bitmap, which is soft at panel sizes.
    /// </summary>
    private static ImageSource? FromShellItemJumbo(string path)
    {
        if (Native.SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            return null;

        try
        {
            var info = new Native.SHFILEINFO();
            const uint SHGFI_PIDL = 0x8;
            if (Native.SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(),
                    SHGFI_PIDL | SHGFI_SYSICONINDEX) == IntPtr.Zero)
                return null;

            Native.IImageList? images = null;
            try
            {
                var iid = typeof(Native.IImageList).GUID;
                if (Native.SHGetImageList(SHIL_JUMBO, ref iid, out images) != 0 || images is null)
                    return null;
                if (images.GetIcon(info.iIcon, ILD_TRANSPARENT, out var icon) != 0 || icon == IntPtr.Zero)
                    return null;
                return FromHandle(icon);
            }
            catch { return null; }
            finally
            {
                if (images is not null && Marshal.IsComObject(images)) Marshal.ReleaseComObject(images);
            }
        }
        finally { Native.ILFree(pidl); }
    }

    /// <summary>Gets the Shell's 256px image-list icon, matching Explorer's large icon artwork.</summary>
    private static ImageSource? FromJumboShellIcon(string path)
    {
        var info = new Native.SHFILEINFO();
        var handle = Native.SHGetFileInfo(path, 0, ref info,
            (uint)Marshal.SizeOf<Native.SHFILEINFO>(), SHGFI_SYSICONINDEX);
        if (handle == IntPtr.Zero) return null;

        Native.IImageList? images = null;
        try
        {
            var iid = typeof(Native.IImageList).GUID;
            if (Native.SHGetImageList(SHIL_JUMBO, ref iid, out images) != 0 || images is null)
                return null;
            if (images.GetIcon(info.iIcon, ILD_TRANSPARENT, out var icon) != 0 || icon == IntPtr.Zero)
                return null;
            return FromHandle(icon);
        }
        catch { return null; }
        finally
        {
            if (images is not null && Marshal.IsComObject(images)) Marshal.ReleaseComObject(images);
        }
    }

    /// <summary>Gets the icon image at the requested size from the Windows Shell.</summary>
    private static ImageSource? FromShellImage(string path)
    {
        var px = IconPixels;
        var iid = typeof(Native.IShellItemImageFactory).GUID;
        if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            return null;

        try
        {
            var hr = factory.GetImage(new Native.SIZE { cx = px, cy = px },
                SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero,
                    System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { Native.DeleteObject(hBitmap); }
        }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(factory); }
    }

    /// <summary>
    /// Real high-resolution icons extracted from the source files: PE resources for
    /// exe/dll/ico, the registered DefaultIcon for documents, imageres.dll for folders.
    /// </summary>
    private static ImageSource? FromHighResFallback(string path)
    {
        var px = IconPixels;
        if (TryExtractFromFile(path, px, out var source)) return source;
        if (TryExtractFromAssociation(path, px, out source)) return source;
        if (TryExtractFolderIcon(path, px, out source)) return source;
        return null;
    }

    /// <summary>exe/dll/ico: read the icon straight out of the PE resources.</summary>
    private static bool TryExtractFromFile(string path, int px, out ImageSource? source)
    {
        source = null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".exe" and not ".dll" and not ".ico") return false;
        return TryExtractIcons(path, 0, px, out source);
    }

    /// <summary>Documents: resolve the registered DefaultIcon of their file type and extract it.</summary>
    private static bool TryExtractFromAssociation(string path, int px, out ImageSource? source)
    {
        source = null;
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || ext is ".exe" or ".dll" or ".ico") return false;

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

            if (string.IsNullOrWhiteSpace(iconRef)) return false;

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
            if (string.IsNullOrEmpty(file) || file.StartsWith("@") || !File.Exists(file)) return false;
            return TryExtractIcons(file, index, px, out source);
        }
        catch { return false; }
    }

    /// <summary>Folders: the system folder icon lives in imageres.dll (index 2 on Win10/11).</summary>
    private static bool TryExtractFolderIcon(string path, int px, out ImageSource? source)
    {
        source = null;
        if (!Directory.Exists(path)) return false;
        var imageres = Path.Combine(Environment.SystemDirectory, "imageres.dll");
        return File.Exists(imageres) && TryExtractIcons(imageres, 2, px, out source);
    }

    /// <summary>Extracts one HICON from a PE file's resources at the requested size.</summary>
    private static bool TryExtractIcons(string file, int index, int px, out ImageSource? source)
    {
        source = null;
        var handles = new IntPtr[1];
        if (PrivateExtractIconsW(file, index, px, px, handles, null, 1, 0) == 0 || handles[0] == IntPtr.Zero)
        {
            if (handles[0] != IntPtr.Zero) Native.DestroyIcon(handles[0]);
            return false;
        }

        // FromHandle keeps the alpha channel intact (CreateBitmapSourceFromHIcon) and destroys
        // the HICON. The HBITMAP route smeared icon edges with black fringes.
        source = FromHandle(handles[0]);
        return source is not null;
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

    /// <summary>Diagnostic: what each pipeline stage returns for one path, size and ink extent.</summary>
    public static string DiagnoseSizes(string path)
    {
        var lines = new List<string> { $"path={path}" };
        if (TryResolveShortcutTarget(path, out var target))
        {
            lines.Add($"target={target}");
            lines.Add($"targetIcon={Describe(Get(target))}");
        }

        lines.Add($"jumbo   : {Describe(FromJumboShellIcon(path))}");
        lines.Add($"shellImg: {Describe(FromShellImage(path))}");
        lines.Add($"highRes : {Describe(FromHighResFallback(path))}");
        lines.Add($"shellIco: {Describe(FromShellIcon(path))}");
        lines.Add($"trimmed : {Describe(TrimTransparentFrame(FromShellIcon(path)))}");
        lines.Add($"chosen  : {Describe(Get(path))}");
        lines.Add($"iconPixels={IconPixels} scale={Native.SystemScale:0.##}");
        return string.Join('\n', lines);
    }

    /// <summary>Diagnostic: writes the icon the app would show for this path to a PNG.</summary>
    public static bool SaveChosen(string path, string pngPath)
    {
        try
        {
            if (Get(path) is not BitmapSource bitmap) return false;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(pngPath);
            encoder.Save(stream);
            return true;
        }
        catch { return false; }
    }

    /// <summary>"256x256 content=16x16 at (0,0)" — the ink box is what the eye actually sees.</summary>
    private static string Describe(ImageSource? image)
    {
        if (image is not BitmapSource bitmap) return "null";

        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        try
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = width * 4;
            var buffer = new byte[stride * height];
            converted.CopyPixels(buffer, stride, 0);

            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    if (buffer[row + x * 4 + 3] <= 8) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            return maxX < 0
                ? $"{width}x{height} (fully transparent)"
                : $"{width}x{height} content={maxX - minX + 1}x{maxY - minY + 1} at ({minX},{minY})";
        }
        catch (Exception ex) { return $"{width}x{height} (probe failed: {ex.GetType().Name})"; }
    }

    /// <summary>
    /// Trims the transparent frame around an icon and returns an image whose ink fills the bitmap.
    /// The Shell hands back a 256px canvas for icons that only exist at 16 or 32px, with the small
    /// picture parked in one corner — stretched into a 48px tile that reads as "a tiny icon shrunk
    /// into the corner" instead of a real one.
    /// </summary>
    private static ImageSource? TrimTransparentFrame(ImageSource? image)
    {
        if (image is not BitmapSource bitmap) return image;

        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        try
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = width * 4;
            var buffer = new byte[stride * height];
            converted.CopyPixels(buffer, stride, 0);

            const int inkThreshold = 8;
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    if (buffer[row + x * 4 + 3] <= inkThreshold) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0) return image;                     // nothing visible: keep as-is
            var inkWidth = maxX - minX + 1;
            var inkHeight = maxY - minY + 1;

            // Only the Shell's "small bitmap parked in the corner of a big canvas" case gets
            // trimmed. Artwork that simply carries generous margins (VLC's cone, a vendor logo)
            // is deliberate — trimming it would make that tile bigger than its neighbours.
            var cornered = minX <= width * 0.06 && minY <= height * 0.06;
            var muchSmaller = inkWidth <= width * 0.72 || inkHeight <= height * 0.72;
            if (!cornered || !muchSmaller) return image;

            var crop = new CroppedBitmap(bitmap, new System.Windows.Int32Rect(minX, minY, inkWidth, inkHeight));
            crop.Freeze();
            return crop;
        }
        catch { return image; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[]? phicon, int[]? piconid, int nIcons, int flags);

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
