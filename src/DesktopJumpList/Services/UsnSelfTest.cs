using System.IO;
using System.Text;
using DesktopJumpList.Infrastructure;

namespace DesktopJumpList.Services;

/// <summary>
/// `--usn-selftest` diagnostic: proves the live-index primitives work in this session — journal
/// access through a directory handle, record parsing, and file-ID directory enumeration. Output
/// goes to stdout and to a text file so it is readable from a scripted run.
/// </summary>
internal static class UsnSelfTest
{
    public static void Run(string directory)
    {
        var sb = new StringBuilder();
        void Line(string text) { sb.AppendLine(text); Console.WriteLine(text); }

        try
        {
            var volumeRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            Line($"volume root: {volumeRoot}");
            Line($"elevated: {IsElevated()}");

            using (var journalHandle = UsnApi.OpenJournal(volumeRoot, out var openError))
            {
                Line($"open journal handle -> {(journalHandle.IsInvalid ? $"FAIL err={openError}" : "OK")}");

                if (!journalHandle.IsInvalid && UsnApi.TryQueryJournal(journalHandle, out var journal, out var queryError))
                {
                    Line($"journal: id={journal.UsnJournalID} first={journal.FirstUsn} next={journal.NextUsn} max={journal.MaximumSize}");

                    var start = Math.Max(journal.FirstUsn, journal.NextUsn - 1_000_000);
                    var records = UsnApi.ReadJournal(journalHandle, start, journal.UsnJournalID, 5000, out var next, out var readError);
                    Line($"read from {start}: {records.Count} records, next={next}, err={readError}");

                    var creates = records.Count(r => (r.Reason & UsnApi.USN_REASON_FILE_CREATE) != 0);
                    var deletes = records.Count(r => (r.Reason & UsnApi.USN_REASON_FILE_DELETE) != 0);
                    var renames = records.Count(r => (r.Reason & (UsnApi.USN_REASON_RENAME_OLD_NAME | UsnApi.USN_REASON_RENAME_NEW_NAME)) != 0);
                    var data = records.Count(r => (r.Reason & (UsnApi.USN_REASON_DATA_OVERWRITE | UsnApi.USN_REASON_DATA_EXTEND)) != 0);
                    Line($"reasons: create={creates} delete={deletes} rename={renames} data={data}");

                    foreach (var record in records.Take(6))
                        Line($"  usn={record.Usn} id={record.FileId} parent={record.ParentId} dir={record.IsDirectory} " +
                             $"reason=0x{record.Reason:X8} v{record.MajorVersion} name={record.Name}");
                }
                else
                {
                    Line($"query journal failed: {(journalHandle.IsInvalid ? openError : -1)}");
                }
            }

            // Real end-to-end check: anchor -> create/modify/rename/delete -> read the delta.
            using (var journalHandle = UsnApi.OpenJournal(volumeRoot, out _))
            {
                if (!journalHandle.IsInvalid && UsnApi.TryQueryJournal(journalHandle, out var anchor, out _))
                {
                    var sandbox = Path.Combine(directory, "delta-probe");
                    Directory.CreateDirectory(sandbox);
                    var fileA = Path.Combine(sandbox, "anchor-a.txt");
                    var fileB = Path.Combine(sandbox, "anchor-b.txt");
                    File.WriteAllText(fileA, "one");
                    File.WriteAllText(fileB, "two");
                    File.Move(fileB, Path.Combine(sandbox, "anchor-renamed.txt"));
                    File.Delete(fileA);
                    Thread.Sleep(120);

                    var delta = UsnApi.ReadJournal(journalHandle, anchor.NextUsn, anchor.UsnJournalID, 2000, out var next, out var deltaError);
                    Line($"delta from anchor {anchor.NextUsn}: {delta.Count} records next={next} err={deltaError}");
                    foreach (var record in delta.Take(10))
                        Line($"  id={record.FileId} parent={record.ParentId} dir={record.IsDirectory} reason=0x{record.Reason:X8} name={record.Name}");

                    foreach (var start in new long[] { 0, anchor.FirstUsn, anchor.FirstUsn + 1, anchor.NextUsn - 16, anchor.NextUsn - 8, anchor.NextUsn })
                    {
                        var probeRecords = UsnApi.ReadJournalRaw(journalHandle, start, anchor.UsnJournalID, 50, out var probeError);
                        Line($"  start={start}: n={probeRecords.Count} err={probeError}");
                    }
                }
            }

            // Access-mask matrix for the journal read: which handle shape is allowed to read?
            foreach (var (label, path, access, flags) in new[]
                     {
                         ("dir GENERIC_READ", volumeRoot, UsnApi.GENERIC_READ, UsnApi.FILE_FLAG_BACKUP_SEMANTICS),
                         ("volume access0", @"\\.\C:", 0u, 0u),
                         ("volume ATTR", @"\\.\C:", UsnApi.FILE_READ_ATTRIBUTES, 0u)
                     })
            {
                using var handle = UsnApi.OpenDevice(path, access, flags, out var err);
                if (handle.IsInvalid) { Line($"{label}: open FAIL err={err}"); continue; }
                var okQuery = UsnApi.TryQueryJournal(handle, out var j, out var qe);
                var detail = "";
                if (okQuery)
                {
                    foreach (var variant in new (string Name, long Start, ulong Id)[]
                             {
                                 ("next-100k", Math.Max(j.FirstUsn, j.NextUsn - 100_000), j.UsnJournalID),
                                 ("first+1", j.FirstUsn + 1, j.UsnJournalID),
                                 ("id0", Math.Max(j.FirstUsn, j.NextUsn - 100_000), 0)
                             })
                    {
                        var recs = UsnApi.ReadJournalRaw(handle, variant.Start, variant.Id, 10, out var re);
                        detail += $" [{variant.Name}: n={recs.Count} err={re}]";
                    }
                }
                Line($"{label}: open OK type={UsnApi.GetFileType(handle)} query={okQuery} qerr={qe}{detail}");
            }

            // File-ID directory enumeration on a real folder.
            var probe = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using (var dir = UsnApi.OpenDirectory(probe))
            {
                if (dir.IsInvalid)
                {
                    Line($"open dir failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                }
                else
                {
                    UsnApi.TryGetIdentity(dir, out var selfId, out _, out _);
                    Line($"dir id for {probe}: {selfId}");
                    var children = UsnApi.ListChildren(dir, 2000, out var listError);
                    Line($"children: {children.Count} (err={listError})");
                    Line($"  dirs={children.Count(c => c.IsDirectory)} files={children.Count(c => !c.IsDirectory)} " +
                         $"reparse={children.Count(c => c.IsReparsePoint)}");
                    foreach (var child in children.Take(4))
                        Line($"  id={child.Id} dir={child.IsDirectory} name={child.Name} write={new DateTime(1601, 1, 1).AddTicks(child.WriteTicks)}");
                }
            }

            Line($"final path of profile: {UsnApi.TryGetFinalPath(probe) ?? "<failed>"}");
            Line($"final path of C:\\Users\\All Users: {UsnApi.TryGetFinalPath(@"C:\Users\All Users") ?? "<failed>"}");
        }
        catch (Exception ex)
        {
            Line("EXCEPTION: " + ex);
        }

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "usn-selftest.txt"), sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
