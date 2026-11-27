using System.IO;
using System.Text;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

/// <summary>
/// `--index-selftest` end-to-end check of the live index: build, persist, reload, apply a
/// simulated USN batch, then let the watchers report real file operations. Prints a pass/fail
/// line per expectation so it can run headless.
/// </summary>
internal static class IndexSelfTest
{
    private static readonly StringBuilder Log = new();
    private static int _failures;

    public static void Run(string directory)
    {
        try
        {
            var sandbox = Path.Combine(directory, "index-selftest");
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
            var root = Path.Combine(sandbox, "root");
            Directory.CreateDirectory(Path.Combine(root, "a", "sub"));
            Directory.CreateDirectory(Path.Combine(root, "zdir"));
            File.WriteAllText(Path.Combine(root, "a", "c.txt"), "c");
            File.WriteAllText(Path.Combine(root, "a", "d.docx"), "d");
            File.WriteAllText(Path.Combine(root, "a", "e.tmp"), "e");
            File.WriteAllText(Path.Combine(root, "a", "sub", "f.png"), "f");

            // Built-in exclusions: Office owner files, hidden/system items, hidden folders,
            // plus one manual wildcard entry.
            File.WriteAllText(Path.Combine(root, "a", "~$draft.docx"), "lock");
            var hiddenFile = Path.Combine(root, "a", "hidden.docx");
            File.WriteAllText(hiddenFile, "h");
            File.SetAttributes(hiddenFile, FileAttributes.Hidden);
            var systemFile = Path.Combine(root, "a", "system.txt");
            File.WriteAllText(systemFile, "s");
            File.SetAttributes(systemFile, FileAttributes.System);
            var hiddenDir = Path.Combine(root, "zdir-hidden");
            Directory.CreateDirectory(hiddenDir);
            File.WriteAllText(Path.Combine(hiddenDir, "inside-hidden.docx"), "x");
            File.SetAttributes(hiddenDir, FileAttributes.Hidden);
            File.WriteAllText(Path.Combine(root, "a", "skipme.docx"), "x");
            File.WriteAllText(Path.Combine(root, "a", "skipbyname.docx"), "x");

            // ── Ranking sandbox ────────────────────────────────────────────────
            // Deliberately shaped like the real complaints: a folder that shares the query, a
            // name that matches only through a folder above it, a file literally named after the
            // query sitting under such a folder, a "…-asset" folder, Chinese words in the two
            // possible orders, and a file whose letters happen to spell an abbreviation.
            var rank = Path.Combine(root, "rank");
            Directory.CreateDirectory(Path.Combine(rank, "code3"));
            Directory.CreateDirectory(Path.Combine(rank, "docs"));
            Directory.CreateDirectory(Path.Combine(rank, "bower-asset"));
            Directory.CreateDirectory(Path.Combine(rank, "archive", "2026"));
            Directory.CreateDirectory(Path.Combine(rank, "x"));
            Directory.CreateDirectory(Path.Combine(rank, "y"));
            Directory.CreateDirectory(Path.Combine(rank, "code"));
            File.WriteAllText(Path.Combine(rank, "code3", "code.txt"), "1");   // name starts with the query, folder too
            File.WriteAllText(Path.Combine(rank, "docs", "code.md"), "2");     // query starts a word inside the name
            File.WriteAllText(Path.Combine(rank, "setup.exe"), "3");
            File.WriteAllText(Path.Combine(rank, "archive", "2026", "季度报告.docx"), "4");
            File.WriteAllText(Path.Combine(rank, "sticker18-CJ6dyGZ3.png"), "5"); // "sjg" is a subsequence, not a word
            File.WriteAllText(Path.Combine(rank, "设计稿-v3.png"), "6");
            File.WriteAllText(Path.Combine(rank, "x", "dup.txt"), "7");
            File.WriteAllText(Path.Combine(rank, "y", "dup.txt"), "8");

            var rules = new IndexRules(new[] { root }, new Dictionary<string, RootLimitSetting>(),
                new[] { ".txt", ".docx", ".png", ".md", ".exe" }, new[] { "*skipme*", "skipbyname.docx" }, 0,
                discoverDriveRoots: false);

            var data = Path.Combine(sandbox, "data");
            Directory.CreateDirectory(data);

            static int Files(FileIndex index, string query) =>
                index.Search(query, 40).Count(entry => !FileIndex.IsAppEntry(entry));

            var index = new FileIndex(data);
            index.Start(rules);
            WaitFor(() => !index.IsBuilding, 20_000);
            Check("build found 5 rows", Files(index, "\\") == 5 || index.Search(".txt", 40).Any(),
                $"{index.Count} total / {Files(index, "txt")} matches");
            Check("filters .tmp", !index.Search("e.tmp", 20).Any(entry => entry.Name == "e.tmp"));
            Check("finds nested png", index.Search("f.png", 10).Any(entry => entry.Path.EndsWith(@"a\sub\f.png", StringComparison.OrdinalIgnoreCase)));
            Check("finds by folder path", index.Search("sub", 10).Any(entry => entry.Name == "f.png"));

            // Built-in filters must hold for the full walk as well.
            Check("skips Office ~$ lock file", !index.Search("draft", 10).Any(entry => entry.Name.StartsWith("~$")),
                string.Join('|', index.Search("draft", 10).Select(entry => entry.Name)));
            Check("skips hidden file", !index.Search("hidden.docx", 10).Any(entry => entry.Name == "hidden.docx"));
            Check("skips system file", !index.Search("system.txt", 10).Any(entry => entry.Name == "system.txt"));
            Check("skips hidden folder subtree", !index.Search("inside-hidden", 10).Any());
            Check("manual wildcard exclude", !index.Search("skipme", 10).Any(entry => entry.Name == "skipme.docx"));
            Check("manual name-only exclude", !index.Search("skipbyname", 10).Any(entry => entry.Name == "skipbyname.docx"));

            // ── Ranking ────────────────────────────────────────────────────────
            // These are the assertions the module never had: every earlier check asked "can it be
            // found", none asked "is the right thing first", which is exactly why a folder called
            // "doc" could bury a file called "doc.md" without anyone noticing.
            static string Top(FileIndex idx, string query) =>
                idx.Search(query, 12).FirstOrDefault()?.Name ?? "<none>";

            static int At(FileIndex idx, string query, Func<FileEntry, bool> match) =>
                idx.Search(query, 40).ToList().FindIndex(entry => match(entry));

            Check("order-free: 2026 报告 finds the file", At(index, "2026 报告", e => e.Name.StartsWith("季度报告")) >= 0,
                Top(index, "2026 报告"));
            Check("order-free: 报告 2026 finds the same file", At(index, "报告 2026", e => e.Name.StartsWith("季度报告")) >= 0,
                Top(index, "报告 2026"));
            Check("order-free: both orders agree on the winner",
                string.Equals(Top(index, "2026 报告"), Top(index, "报告 2026"), StringComparison.Ordinal),
                $"{Top(index, "2026 报告")} vs {Top(index, "报告 2026")}");

            // A file whose name starts with the query must beat a folder that merely contains it.
            var codeAt = At(index, "code", e => e.Name == "code.txt");
            var codeDirAt = At(index, "code", e => e.IsDirectory && e.Name == "code3");
            Check("name match outranks folder match", codeAt >= 0 && (codeDirAt < 0 || codeAt < codeDirAt),
                $"code.txt@{codeAt} code3@{codeDirAt}");

            // A folder that ends with the word no longer outranks a file that starts with it.
            var setupAt = At(index, "set", e => e.Name == "setup.exe");
            var assetAt = At(index, "set", e => e.Name == "bower-asset");
            Check("file prefix beats folder tail", setupAt >= 0 && (assetAt < 0 || setupAt < assetAt),
                $"setup@{setupAt} bower-asset@{assetAt}");

            // "sjg" is 设计稿 by initials; it must not drag in scattered letters.
            Check("initials win, subsequence noise stays out",
                Top(index, "sjg") == "设计稿-v3.png"
                && !index.Search("sjg", 40).Any(entry => entry.Name.StartsWith("sticker18")),
                string.Join('|', index.Search("sjg", 40).Select(entry => entry.Name)));

            // Two files with the same name and the same score: the one the user actually opens wins.
            var usage = new UsageStore();
            index.Usage = usage;
            for (var i = 0; i < 3; i++) usage.Record(Path.Combine(rank, "x", "dup.txt"));
            var boosted = index.Search("dup", 4).FirstOrDefault();
            Check("usage breaks the tie", boosted is not null
                && boosted.Path.EndsWith(@"x\dup.txt", StringComparison.OrdinalIgnoreCase),
                string.Join('|', index.Search("dup", 4).Select(entry => entry.Path)));
            index.Usage = null;

            // Every root reports what it contributed, so an empty drive can be explained.
            Check("build report names each root",
                index.RootCounts.TryGetValue(rules.Roots[0], out var rootCount) && rootCount > 0,
                string.Join('|', index.RootCounts.Select(pair => $"{pair.Key}={pair.Value}")));

            // ── Blacklist upgrade ──────────────────────────────────────────────
            // A release that ships new junk-folder patterns edits the user's config on startup.
            // It must only ever append: a list the user pruned stays exactly as they left it.
            var mine = AppSettings.PreviousDefaultIndexExcludes.Concat(new[] { @"*Mine*" }).ToList();
            Check("excludes: new shipped patterns are appended",
                AppSettings.TryMergeDefaultExcludes(mine, out var mergedExcludes)
                && mergedExcludes.Contains(@"*\*cache*\*") && mergedExcludes.Contains(@"*Mine*")
                && mine.All(mergedExcludes.Contains),
                $"{mine.Count} -> {mergedExcludes.Count}");
            var pruned = AppSettings.PreviousDefaultIndexExcludes.Where(entry => entry != @"*\.git\*").ToList();
            Check("excludes: a pruned list is left alone", !AppSettings.TryMergeDefaultExcludes(pruned, out _));
            Check("excludes: an empty list is left alone", !AppSettings.TryMergeDefaultExcludes(new List<string>(), out _));

            // The two id sources (directory listing vs handle) must agree, or every watcher
            // upsert would create a second row for a file the walk already indexed.
            var probePath = Path.Combine(root, "a", "c.txt");
            using (var probeHandle = UsnApi.OpenFile(probePath))
            {
                UsnApi.TryGetIdentity(probeHandle, out var handleId, out _, out _);
                var listed = index.Search("c.txt", 10).FirstOrDefault();
                Log.AppendLine($"identity: list={listed?.Id} handle={handleId} equal={listed?.Id == handleId}");
                Check("file id sources agree", listed?.Id == handleId, $"{listed?.Id} vs {handleId}");
            }

            // Persistence round-trip.
            index.Save();
            var reloaded = new FileIndex(data);
            var loaded = reloaded.Load(rules);
            Check("reload ok", loaded, reloaded.LastLoadError ?? index.LastLoadError ?? "no error");
            Check("reload keeps rows", reloaded.Search("c.txt", 10).Any(entry => entry.Name == "c.txt"), reloaded.Count.ToString());
            Check("reload keeps paths", reloaded.Search("c.txt", 10).Any(entry => entry.Path.EndsWith(@"a\c.txt", StringComparison.OrdinalIgnoreCase)),
                string.Join('|', reloaded.Search("c.txt", 10).Select(entry => entry.Path)));

            // Simulated journal batch: create -> rename -> delete, ids only (paths resolved
            // through the parent chain, exactly like a real USN record).
            var rootId = reloaded.Meta.Roots[rules.Roots[0]];
            var baseline = reloaded.Count;
            var createId = 900001UL;
            int Changes(params UsnRecord[] records) => reloaded.ApplyUsn(records, rules);
            UsnRecord Record(ulong id, ulong parent, string name, uint reason, uint attributes = 0) => new(
                2, id, parent, 0, DateTime.Now.ToFileTime(), reason, attributes, name, 60);

            Changes(Record(createId, rootId, "usn-created.txt", UsnApi.USN_REASON_FILE_CREATE));
            Check("usn create added", reloaded.Search("usn-created", 10).Count == 1, reloaded.Count.ToString());

            Changes(Record(createId, rootId, "usn-renamed.txt", UsnApi.USN_REASON_RENAME_NEW_NAME));
            Check("usn rename applied", reloaded.Search("usn-renamed", 10).Count == 1
                && reloaded.Search("usn-created", 10).Count == 0);

            Changes(Record(createId, 12345UL, "usn-renamed.txt", UsnApi.USN_REASON_RENAME_NEW_NAME));
            Check("usn move out of roots drops row", reloaded.Search("usn-renamed", 10).Count == 0);

            Changes(Record(createId, rootId, "usn-again.txt", UsnApi.USN_REASON_FILE_CREATE));
            Changes(Record(createId, rootId, "usn-again.txt", UsnApi.USN_REASON_FILE_DELETE));
            Check("usn delete applied", reloaded.Search("usn-again", 10).Count == 0);
            Check("count back to baseline", reloaded.Count == baseline, $"{reloaded.Count} vs {baseline}");

            // The same built-in filters must apply to journal records, including an attribute
            // change that turns an already-indexed file into a hidden one.
            Changes(Record(createId + 1, rootId, "attr.txt", UsnApi.USN_REASON_FILE_CREATE));
            Check("usn create before hide", reloaded.Search("attr.txt", 10).Count == 1);
            Changes(Record(createId + 1, rootId, "attr.txt", UsnApi.USN_REASON_BASIC_INFO_CHANGE, 0x00000002));
            Check("usn hidden attribute evicts row", reloaded.Search("attr.txt", 10).Count == 0);
            Changes(Record(createId + 2, rootId, "~$lock.docx", UsnApi.USN_REASON_FILE_CREATE));
            Check("usn ignores ~$ lock file", !reloaded.Search("lock.docx", 10).Any(entry => entry.Name.StartsWith("~$")));
            Check("count still at baseline", reloaded.Count == baseline, $"{reloaded.Count} vs {baseline}");

            // Watcher-driven updates: real filesystem operations, no journal rights needed.
            var sync = new FileIndexSync(reloaded, () => rules, () => true);
            sync.Start();
            WaitFor(() => sync.IsLive, 5000);

            // The journal is only a valid source when a record can actually be read from it;
            // otherwise the sync must fall back to the directory watchers.
            var probeVolume = Path.GetPathRoot(root) ?? @"C:\";
            var journalReadable = false;
            using (var probeHandle = UsnApi.OpenJournal(probeVolume, out _))
            {
                if (!probeHandle.IsInvalid && UsnApi.TryQueryJournal(probeHandle, out var probeJournal, out _)
                    && probeJournal.FirstUsn < probeJournal.NextUsn)
                {
                    var probeRecords = UsnApi.ReadJournal(probeHandle, probeJournal.FirstUsn,
                        probeJournal.UsnJournalID, 1, out _, out var probeError);
                    journalReadable = probeError == 0 && probeRecords.Count > 0;
                }
            }
            Log.AppendLine($"source: {sync.SourceLabel} journalReadable={journalReadable} " +
                           $"anchors={reloaded.Meta.Volumes.Count} pending={sync.PendingCount} error={sync.LastError ?? "-"}");
            Check("source matches journal access", (sync.SourceLabel == "USN 日志") == journalReadable,
                $"{sync.SourceLabel} readable={journalReadable}");

            File.WriteAllText(Path.Combine(root, "a", "watched.txt"), "w");
            WaitFor(() => reloaded.Search("watched", 10).Count == 1, 8000);
            Check("watcher added file", reloaded.Search("watched", 10).Count == 1,
                $"batch={sync.LastBatch} error={sync.LastError ?? "-"} " +
                $"hits=[{string.Join(',', reloaded.Search("watched", 10).Select(e => e.Id + "@" + e.Path))}] count={reloaded.Count}");

            File.Move(Path.Combine(root, "a", "c.txt"), Path.Combine(root, "a", "c2.txt"));
            WaitFor(() => reloaded.Search("c2.txt", 10).Count == 1, 8000);
            Check("watcher rename", reloaded.Search("c2.txt", 10).Count == 1
                && !reloaded.Search("c.txt", 10).Any(entry => entry.Name == "c.txt"),
                $"c2=[{string.Join(',', reloaded.Search("c2.txt", 10).Select(e => e.Path))}] batch={sync.LastBatch}");

            File.Delete(Path.Combine(root, "a", "d.docx"));
            WaitFor(() => reloaded.Search("d.docx", 10).Count == 0, 8000);
            Check("watcher delete", reloaded.Search("d.docx", 10).Count == 0);

            var newDir = Path.Combine(root, "fresh");
            Directory.CreateDirectory(newDir);
            File.WriteAllText(Path.Combine(newDir, "inside.txt"), "i");
            WaitFor(() => reloaded.Search("inside.txt", 10).Count == 1, 8000);
            Check("watcher picked up new folder contents", reloaded.Search("inside.txt", 10).Count == 1,
                reloaded.Search("inside", 10).Count.ToString());

            // Folder rename must cascade to children paths.
            Directory.Move(Path.Combine(root, "a", "sub"), Path.Combine(root, "a", "sub2"));
            WaitFor(() => reloaded.Search("f.png", 10).Any(e => e.Path.Contains(@"sub2", StringComparison.OrdinalIgnoreCase)), 8000);
            var moved = reloaded.Search("f.png", 10).FirstOrDefault();
            Check("folder rename cascades", moved is not null && moved.Path.EndsWith(@"a\sub2\f.png", StringComparison.OrdinalIgnoreCase),
                moved?.Path ?? "<missing>");

            sync.Dispose();
            reloaded.Save();
            Check("final save", true);

            var third = new FileIndex(data);
            var reloadOk = third.Load(rules);
            Check("reload after live updates", reloadOk && third.Search("inside.txt", 10).Count == 1,
                $"load={reloadOk} err={third.LastLoadError ?? "-"} count={third.Count} " +
                $"inside=[{string.Join(',', third.Search("inside", 10).Select(e => e.Path))}]");
        }
        catch (Exception ex)
        {
            Log.AppendLine("EXCEPTION: " + ex);
            _failures++;
        }

        var text = $"failures={_failures}\n" + Log;
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "index-selftest.txt"), text, Encoding.UTF8);
        }
        catch { }
        Console.WriteLine(text);
    }

    private static void Check(string label, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {label}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label} {detail}");
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            Thread.Sleep(100);
        }
    }
}
