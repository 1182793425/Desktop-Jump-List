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

    /// <summary>Diagnostic trace of the last FromShellImage failure (empty on success).</summary>
    public static string LastPipelineNote { get; private set; } = string.Empty;

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
            // shell:AppsFolder\AUMID entries (from the /s index) have no file behind them, so
            // skip the existence probe for them — the image factory resolves the item directly.
            var isShellItem = path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
            if (isShellItem || File.Exists(path) || Directory.Exists(path))
            {
                result = FromShellImage(path)
                    ?? FromHighResFallback(path)
                    ?? FromShellIcon(path);
            }
        }
        catch { result = null; }

        lock (Gate) { Cache[path] = result; }
        return result;
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
    /// Asks the shell for the item's real 256 px icon. SHGetFileInfo only exposes the
    /// 32 px "large" icon, which is exactly why tiles used to look blurry at 40-48 px.
    /// </summary>
    private static ImageSource? FromShellImage(string path)
    {
        var px = IconPixels;
        // Route 1: parse straight to the image factory. On some setups the QI answers
        // E_NOINTERFACE, so route 2 goes through IShellItem + BHID_ThumbnailHandler (the
        // Explorer thumbnail pipeline) with the same GetImage call at the end.
        if (TryGetFactoryDirect(path, px, out var source, out var note))
        {
            LastPipelineNote = note;
            return source;
        }
        var directNote = note;
        if (TryGetFactoryViaHandler(path, px, out source, out note))
        {
            LastPipelineNote = note;
            return source;
        }
        var handlerNote = note;
        LastPipelineNote = $"{directNote} | {handlerNote}";
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

    private static bool TryGetFactoryDirect(string path, int px, out ImageSource? source, out string note)
    {
        source = null;
        var iid = typeof(Native.IShellItemImageFactory).GUID;
        var createHr = Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (createHr != 0 || factory is null)
        {
            note = $"create hr=0x{createHr:X8}";
            return false;
        }

        try
        {
            var hr = factory.GetImage(
                new Native.SIZE { cx = px, cy = px },
                SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK,
                out var hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                note = $"getimage hr=0x{hr:X8}";
                return false;
            }

            try
            {
                note = $"ok {px}px";
                source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return true;
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

    private static bool TryGetFactoryViaHandler(string path, int px, out ImageSource? source, out string note)
    {
        source = null;
        var iidItem = IidIShellItem;
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out var item);
        if (hr != 0 || item is null)
        {
            note = $"item hr=0x{hr:X8}";
            return false;
        }

        var bhid = BhidThumbnailHandler;
        var iidFactory = typeof(Native.IShellItemImageFactory).GUID;
        var bindHr = item.BindToHandler(IntPtr.Zero, ref bhid, ref iidFactory, out var objPtr);
        if (bindHr != 0 || objPtr == IntPtr.Zero)
        {
            note = $"bind hr=0x{bindHr:X8}";
            return false;
        }

        try
        {
            var factory = (Native.IShellItemImageFactory)Marshal.GetTypedObjectForIUnknown(objPtr, typeof(Native.IShellItemImageFactory));
            try
            {
                var getImageHr = factory.GetImage(
                    new Native.SIZE { cx = px, cy = px },
                    SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK,
                    out var hBitmap);
                if (getImageHr != 0 || hBitmap == IntPtr.Zero)
                {
                    note = $"getimage hr=0x{getImageHr:X8}";
                    return false;
                }

                try
                {
                    note = $"ok {px}px (handler)";
                    source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return true;
                }
                finally
                {
                    Native.DeleteObject(hBitmap);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(factory);
            }
        }
        finally
        {
            Marshal.Release(objPtr);
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

    // ───────────────── IShellItem fallback interop ─────────────────

    private static readonly Guid IidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid BhidThumbnailHandler = new("7AAEC32D-C52A-424F-9BC4-39C069E88EFF");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemForIcon ppv);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[]? phicon, int[]? piconid, int nIcons, int flags);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemForIcon
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        // Remaining vtable slots are unused and therefore not declared.
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
}
