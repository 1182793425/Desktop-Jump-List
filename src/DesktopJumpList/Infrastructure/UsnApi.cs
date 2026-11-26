using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopJumpList.Infrastructure;

/// <summary>
/// The handful of Win32 calls the live index needs: NTFS file IDs, the USN journal, and the
/// background-mode thread hint. Everything here is read-only — the index never writes to the
/// volume, so a plain (unelevated) process can use it when the volume handle opens at all.
/// </summary>
internal static class UsnApi
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_READ_ATTRIBUTES = 0x00000080;
    public const uint FILE_LIST_DIRECTORY = 0x00000001;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_FLAG_POSIX_SEMANTICS = 0x01000000;

    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    /// <summary>Same as READ_USN_JOURNAL but allowed for an unelevated caller.</summary>
    public const uint FSCTL_READ_UNPRIVILEGED_USN_JOURNAL = 0x000900EB;

    public const int FileIdBothDirectoryInfo = 10;
    public const int FileIdBothDirectoryRestartInfo = 11;

    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    // USN reason bits we care about.
    public const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
    public const uint USN_REASON_DATA_EXTEND = 0x00000002;
    public const uint USN_REASON_DATA_TRUNCATION = 0x00000004;
    public const uint USN_REASON_FILE_CREATE = 0x00000100;
    public const uint USN_REASON_FILE_DELETE = 0x00000200;
    public const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    public const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    public const uint USN_REASON_BASIC_INFO_CHANGE = 0x00008000;
    public const uint USN_REASON_HARD_LINK_CHANGE = 0x00010000;
    public const uint USN_REASON_STREAM_CHANGE = 0x00200000;
    public const uint USN_REASON_CLOSE = 0x80000000;

    /// <summary>Bits that can change what the index should hold (content/name/location).</summary>
    public const uint UsnActionableMask =
        USN_REASON_DATA_OVERWRITE | USN_REASON_DATA_EXTEND | USN_REASON_DATA_TRUNCATION |
        USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE |
        USN_REASON_RENAME_OLD_NAME | USN_REASON_RENAME_NEW_NAME |
        USN_REASON_BASIC_INFO_CHANGE | USN_REASON_HARD_LINK_CHANGE | USN_REASON_STREAM_CHANGE;

    public const uint THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
        IntPtr buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, ref BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, char[] path, uint length, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    /// <summary>
    /// Native layout is DWORD-aligned throughout (FILETIME is two DWORDs), so Pack = 4 is what
    /// keeps FileIndexHigh/Low at offsets 44/48 — the default 8-byte alignment would shift them
    /// and every id read back here would be garbage.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct USN_JOURNAL_DATA
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct READ_USN_JOURNAL_DATA
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    /// <summary>
    /// V1 adds the record-version window, and the unprivileged FSCTL insists on it: passing the
    /// V0 size there fails with ERROR_INVALID_PARAMETER.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct READ_USN_JOURNAL_DATA_V1
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
        public ushort MinMajorVersion;
        public ushort MaxMajorVersion;
    }

    /// <summary>
    /// Opens a handle suitable for USN journal queries.
    /// <para>
    /// A raw volume handle (<c>\\.\C:</c>) needs administrator rights on most systems, and on
    /// some configurations it opens yet every FSCTL answers ERROR_INVALID_FUNCTION. A plain
    /// directory handle on the same volume accepts the journal FSCTLs just as well and opens
    /// unelevated, which is what makes live indexing work for a normal user.
    /// </para>
    /// </summary>
    public static SafeFileHandle OpenJournal(string volumeRoot, out int error)
    {
        error = 0;
        var directory = OpenDirectory(volumeRoot);
        if (!directory.IsInvalid) return directory;

        error = Marshal.GetLastWin32Error();
        var volume = OpenVolume(volumeRoot, out var volumeError);
        if (!volume.IsInvalid) return volume;
        error = volumeError;
        return volume;
    }

    /// <summary>Opens a volume for the journal read. Falls back to attribute-only access.</summary>
    public static SafeFileHandle OpenVolume(string volume, out int error)
    {
        error = 0;
        var path = volume.EndsWith('\\') && volume.Length == 3 ? @"\\.\" + volume[..2] : @"\\.\" + volume.TrimEnd('\\');
        foreach (var access in new[] { GENERIC_READ, FILE_READ_ATTRIBUTES, 0u })
        {
            var handle = CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                error = 0;
                return handle;
            }
            error = Marshal.GetLastWin32Error();
            // 5 = access denied: try a weaker access mask before giving up.
            if (error != 5) break;
        }
        return new SafeFileHandle(IntPtr.Zero, true);
    }

    public static SafeFileHandle OpenDirectory(string path) =>
        CreateFileW(path, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_POSIX_SEMANTICS, IntPtr.Zero);

    /// <summary>Opens a file just far enough to read its identity and timestamps.</summary>
    public static SafeFileHandle OpenFile(string path) =>
        CreateFileW(path, FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_POSIX_SEMANTICS, IntPtr.Zero);

    /// <summary>Explicit open, used by the diagnostic to compare access masks.</summary>
    public static SafeFileHandle OpenDevice(string path, uint access, uint flags, out int error)
    {
        var handle = CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return handle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetFileType(SafeFileHandle handle);

    public static bool TryQueryJournal(SafeFileHandle volume, out USN_JOURNAL_DATA data, out int error)
    {
        data = default;
        var buffer = Marshal.AllocHGlobal(128);
        try
        {
            // Zero the tail so a V1-sized structure is fully initialised.
            for (var i = 0; i < 128; i++) Marshal.WriteByte(buffer, i, 0);
            if (!DeviceIoControl(volume, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, buffer, 128, out _, IntPtr.Zero))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            data = Marshal.PtrToStructure<USN_JOURNAL_DATA>(buffer);
            error = 0;
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// Reads up to <paramref name="maxRecords"/> journal entries starting at
    /// <paramref name="startUsn"/>. Returns the next USN to ask for.
    /// </summary>
    public static List<UsnRecord> ReadJournal(SafeFileHandle volume, long startUsn, ulong journalId,
        int maxRecords, out long nextUsn, out int error)
    {
        var records = ReadJournalCore(volume, startUsn, journalId, maxRecords, false, out nextUsn, out error);
        // 5 = access denied: an unelevated process may only use the unprivileged variant.
        if (error == 5 || error == 1)
            records = ReadJournalCore(volume, startUsn, journalId, maxRecords, true, out nextUsn, out error);
        return records;
    }

    /// <summary>Unprivileged-only read, used by the diagnostic to report the exact error.</summary>
    public static List<UsnRecord> ReadJournalRaw(SafeFileHandle handle, long startUsn, ulong journalId,
        int maxRecords, out int error) =>
        ReadJournalCore(handle, startUsn, journalId, maxRecords, true, out _, out error);

    private static List<UsnRecord> ReadJournalCore(SafeFileHandle volume, long startUsn, ulong journalId,
        int maxRecords, bool unprivileged, out long nextUsn, out int error)
    {
        var records = new List<UsnRecord>(Math.Min(maxRecords, 512));
        nextUsn = startUsn;
        error = 0;
        var input = new READ_USN_JOURNAL_DATA
        {
            StartUsn = startUsn,
            ReasonMask = 0xFFFFFFFF,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = journalId
        };

        var inSize = unprivileged ? Marshal.SizeOf<READ_USN_JOURNAL_DATA_V1>() : Marshal.SizeOf<READ_USN_JOURNAL_DATA>();
        var inBuffer = Marshal.AllocHGlobal(inSize);
        var outBuffer = Marshal.AllocHGlobal(OutputSize);
        try
        {
            if (unprivileged)
            {
                // V1 explicitly asks for record versions 2..4; without it the FSCTL rejects
                // the call with ERROR_INVALID_PARAMETER.
                var v1 = new READ_USN_JOURNAL_DATA_V1
                {
                    StartUsn = startUsn,
                    ReasonMask = 0xFFFFFFFF,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = journalId,
                    MinMajorVersion = 2,
                    MaxMajorVersion = 4
                };
                Marshal.StructureToPtr(v1, inBuffer, false);
            }
            else
            {
                Marshal.StructureToPtr(input, inBuffer, false);
            }

            var code = unprivileged ? FSCTL_READ_UNPRIVILEGED_USN_JOURNAL : FSCTL_READ_USN_JOURNAL;
            if (!DeviceIoControl(volume, code, inBuffer,
                    inSize, outBuffer, OutputSize, out var returned, IntPtr.Zero))
            {
                error = Marshal.GetLastWin32Error();
                return records;
            }
            if (returned < 8) return records;

            nextUsn = Marshal.ReadInt64(outBuffer);
            var offset = 8;
            while (offset + 60 <= returned && records.Count < maxRecords)
            {
                var length = Marshal.ReadInt32(outBuffer, offset);
                if (length < 60 || offset + length > returned) break;
                records.Add(Parse(outBuffer, offset, length));
                offset += length;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
        return records;
    }

    private const int OutputSize = 512 * 1024;

    private static UsnRecord Parse(IntPtr buffer, int offset, int length)
    {
        var major = (ushort)Marshal.ReadInt16(buffer, offset + 4);
        // Only V2 (60-byte header) is produced on NTFS; V3 differs in the final fields and is
        // mapped to the same three values we actually use.
        var fileId = (ulong)Marshal.ReadInt64(buffer, offset + 8);
        var parentId = (ulong)Marshal.ReadInt64(buffer, offset + 16);
        var usn = Marshal.ReadInt64(buffer, offset + 24);
        var stamp = Marshal.ReadInt64(buffer, offset + 32);
        var reason = (uint)Marshal.ReadInt32(buffer, offset + 40);
        var attributes = (uint)Marshal.ReadInt32(buffer, offset + 52);
        var nameLength = (ushort)Marshal.ReadInt16(buffer, offset + 56);
        var nameOffset = (ushort)Marshal.ReadInt16(buffer, offset + 58);
        var name = nameLength == 0 ? string.Empty : Marshal.PtrToStringUni(buffer + offset + nameOffset, nameLength / 2) ?? string.Empty;
        return new UsnRecord(major, fileId, parentId, usn, stamp, reason, attributes, name, length);
    }

    /// <summary>One FILE_ID_BOTH_DIR_INFO child, in the shape the walker wants.</summary>
    public readonly struct DirChild
    {
        public DirChild(ulong id, string name, uint attributes, long writeTicks)
        {
            Id = id; Name = name; Attributes = attributes; WriteTicks = writeTicks;
        }
        public ulong Id { get; }
        public string Name { get; }
        public uint Attributes { get; }
        public long WriteTicks { get; }
        public bool IsDirectory => (Attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        public bool IsReparsePoint => (Attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    /// <summary>
    /// Enumerates one directory with file IDs and write times in a single pass — no per-file
    /// open, which is what makes a full rebuild of a large tree affordable.
    /// </summary>
    public static List<DirChild> ListChildren(SafeFileHandle directory, int max, out int error)
    {
        var result = new List<DirChild>(64);
        error = 0;
        var buffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            var restart = true;
            while (result.Count < max)
            {
                var cls = restart ? FileIdBothDirectoryRestartInfo : FileIdBothDirectoryInfo;
                restart = false;
                if (!GetFileInformationByHandleEx(directory, cls, buffer, 64 * 1024))
                {
                    error = Marshal.GetLastWin32Error();
                    break;
                }

                var offset = 0;
                while (true)
                {
                    var next = Marshal.ReadInt32(buffer, offset);
                    var nameLength = Marshal.ReadInt32(buffer, offset + 60);
                    var attributes = (uint)Marshal.ReadInt32(buffer, offset + 56);
                    var writeTicks = Marshal.ReadInt64(buffer, offset + 24);
                    var fileId = (ulong)Marshal.ReadInt64(buffer, offset + 96);
                    if (nameLength > 0)
                    {
                        var name = Marshal.PtrToStringUni(buffer + offset + 104, nameLength / 2) ?? string.Empty;
                        // "." and ".." are not real children.
                        if (name != "." && name != "..")
                            result.Add(new DirChild(fileId, name, attributes, writeTicks));
                    }
                    if (next == 0 || result.Count >= max) break;
                    offset += next;
                    if (offset + 104 > 64 * 1024) break;
                }

                if (result.Count >= max) break;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    public static bool TryGetIdentity(SafeFileHandle file, out ulong id, out uint attributes, out long writeTicks)
    {
        id = 0;
        attributes = 0;
        writeTicks = 0;
        var info = new BY_HANDLE_FILE_INFORMATION();
        if (!GetFileInformationByHandle(file, ref info)) return false;
        id = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        attributes = info.FileAttributes;
        writeTicks = info.LastWriteTime;
        return id != 0;
    }

    /// <summary>
    /// The real path behind a handle. Opening through a junction (All Users → ProgramData) yields
    /// the target here, which is what lets the blacklist catch aliases the logical path hides.
    /// </summary>
    public static string? TryGetFinalPath(SafeFileHandle file)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) return null;
        var path = new string(buffer, 0, (int)length);
        // Strip the \\?\ prefix used by the API.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return path;
    }

    public static string? TryGetFinalPath(string path)
    {
        using var handle = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        return TryGetFinalPath(handle);
    }

    /// <summary>
    /// Puts the calling thread into background mode: CPU below normal *and* I/O at the lowest
    /// priority, so a rebuild never competes with whatever the user is doing.
    /// </summary>
    public static bool EnterBackgroundMode()
    {
        try
        {
            SetThreadPriority(GetCurrentThread(), -2 /* THREAD_PRIORITY_LOWEST */);
            return SetThreadPriority(GetCurrentThread(), unchecked((int)THREAD_MODE_BACKGROUND_BEGIN));
        }
        catch { return false; }
    }

    public static void LeaveBackgroundMode()
    {
        try { SetThreadPriority(GetCurrentThread(), unchecked((int)0x00020000) /* THREAD_MODE_BACKGROUND_END */); }
        catch { }
    }
}

/// <summary>One parsed USN_RECORD_V2. Only the fields the index needs are kept.</summary>
public readonly record struct UsnRecord(
    ushort MajorVersion,
    ulong FileId,
    ulong ParentId,
    long Usn,
    long TimeStamp,
    uint Reason,
    uint Attributes,
    string Name,
    int Length)
{
    public bool IsDirectory => (Attributes & UsnApi.FILE_ATTRIBUTE_DIRECTORY) != 0;
    public bool IsReparsePoint => (Attributes & UsnApi.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    public DateTime Time => TimeStamp > 0
        ? DateTime.FromFileTimeUtc(TimeStamp).ToLocalTime()
        : DateTime.MinValue;
}
