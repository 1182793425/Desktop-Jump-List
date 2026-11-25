using System.Runtime.InteropServices;

namespace DesktopJumpList.Services;

/// <summary>
/// Enumerates shell:AppsFolder — every launchable entry the Start menu knows (UWP apps,
/// aliased Windows tools such as 便签 / 录音机 / 计算器), the same source as Get-StartApps.
/// PackageManager's FindPackages* APIs are access-denied for unpackaged processes, so the
/// Shell namespace is the dependable route; it works from any process.
/// Icons come through IconService.Get(shell:AppsFolder\AUMID) — the exact pipeline the main
/// tiles use — so this service only deals with names and ids.
/// </summary>
public static class AppsFolderService
{
    public sealed record AppEntry(string Name, string AumId);

    public static List<AppEntry> Enumerate()
    {
        var list = new List<AppEntry>();
        try
        {
            SHParseDisplayName("shell:AppsFolder", IntPtr.Zero, out var folderPidl, 0, out _);
            if (folderPidl == IntPtr.Zero) return list;

            try
            {
                SHGetDesktopFolder(out var desktop);
                var iidFolder = IidIShellFolder;
                desktop.BindToObject(folderPidl, IntPtr.Zero, ref iidFolder, out var folder);
                folder.EnumObjects(IntPtr.Zero, SHCONTF_FOLDERS | SHCONTF_NONFOLDERS, out var enumList);

                var child = new IntPtr[1];
                var iidItem = IidIShellItem;
                while (enumList.Next(1, child, out var fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        SHCreateItemWithParent(folderPidl, null, child[0], ref iidItem, out var item);
                        var name = DisplayName(item, SIGDN_NORMALDISPLAY);
                        // Parent-relative parsing name of an AppsFolder entry is its AUMID
                        // (Microsoft.WindowsCalculator_...!App) or an alias path.
                        var aumid = DisplayName(item, SIGDN_PARENTRELATIVEPARSING);
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(aumid))
                            list.Add(new AppEntry(name, aumid));
                    }
                    catch { /* skip broken entries */ }
                }
            }
            finally { ILFree(folderPidl); }
        }
        catch { /* Shell unavailable — caller falls back to file results only */ }

        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? DisplayName(IShellItem item, uint sigdn)
    {
        item.GetDisplayName(sigdn, out var buffer);
        if (buffer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }

    // ───────────────────────── Shell interop ─────────────────────────

    private static readonly Guid IidIShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    private const int SHCONTF_FOLDERS = 0x20;
    private const int SHCONTF_NONFOLDERS = 0x40;
    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, string pszDisplayName, out uint pchEaten, out IntPtr ppidl, IntPtr pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IEnumIDList ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv);
        // Remaining vtable slots are unused and therefore not declared.
    }

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIDList
    {
        [PreserveSig]
        int Next(uint celt,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] rgelt,
            out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumIDList ppenum);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        // Remaining vtable slots are unused and therefore not declared.
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out IShellFolder ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemWithParent(IntPtr pidlParent, IShellFolder? psfParent, IntPtr pidlChild, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);
}
