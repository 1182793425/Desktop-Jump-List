using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;
using DesktopJumpList.Services;

namespace DesktopJumpList.Views;

/// <summary>
/// The "/" surface. Two things live here: a tiny command list (/settings) and the "/s" mode,
/// which switches the whole result surface to local files and nothing else.
/// A plain query never reaches this file's index code — that is the whole point of /s.
/// </summary>
public partial class LauncherWindow
{
    private const string SlashSettings = "settings";
    private const int FileResultPageSize = 30;

    private string? _lastSlashSignature;
    private string? _fileResultQuery;
    private int _fileResultsLoaded;
    /// <summary>Ranked hits already handed to the list — the offset of the next page.</summary>
    private int _fileResultsSkip;
    private int _fileQueryGeneration;
    private bool _fileResultsHasMore;
    private bool _fileLoadingMore;

    // "/f" / "/file" also surface the /s command in the palette so it is discoverable.
    private static readonly string[] FileCommandAliases =
        { "s", "f", "file", "files", "本地文件", "检索" };

    /// <summary>
    /// "/s" or "/s something" enters local-file mode. The character after the "s" must be
    /// whitespace or the end of the string, so "/settings" and "/se" stay commands instead of
    /// being swallowed by the /s prefix.
    /// </summary>
    private static bool TryParseFileQuery(string query, out string terms)
    {
        terms = string.Empty;
        var body = query.TrimStart('/');
        if (body.Length == 0 || (body[0] != 's' && body[0] != 'S')) return false;
        if (body.Length > 1 && !char.IsWhiteSpace(body[1])) return false;
        terms = body[1..].Trim();
        return true;
    }

    private void RebuildSlash(string query)
    {
        var rows = SlashRows(query);
        ShowSlashRows(rows, rows.Count == 0 ? $"没有这个命令：{query}" : string.Empty);
    }

    /// <summary>"/s" results: matching local files and apps, loaded in scrollable pages.</summary>
    private void RebuildFileSearch(string terms)
    {
        _fileQueryGeneration++;
        _fileResultQuery = terms;
        _fileResultsLoaded = 0;
        _fileResultsSkip = 0;
        _fileResultsHasMore = false;
        _fileLoadingMore = false;
        var rows = new List<ResultRow>();
        var index = App.Current.Files;
        string emptyText;
        if (!CurrentSettings.FileSearchEnabled)
        {
            emptyText = "本地文件搜索已关闭，可在设置的「本地文件索引」中启用";
        }
        else if (index.IsBuilding)
        {
            emptyText = "正在重建索引…";
        }
        else if (index.LastBuilt == DateTime.MinValue)
        {
            // The index is never built on its own: without a manual rebuild there is nothing to
            // search, so say exactly where the button is.
            emptyText = "尚未建立索引：点右上角 ⟳ 或按 Ctrl+Enter 手动重建";
        }
        else if (index.Count == 0)
        {
            emptyText = "索引中没有可搜索的项目，可检查索引目录与白名单后重建";
        }
        else
        {
            emptyText = "正在搜索本地索引…";
            _lastSlashSignature = null;
            ShowSlashRows(rows, emptyText);
            UpdateFileSearchChrome();
            UpdateFileSearchCount();
            LoadInitialFileResults(terms, _fileQueryGeneration, index);
            return;
        }

        _lastSlashSignature = null;
        ShowSlashRows(rows, emptyText);
        UpdateFileSearchChrome();
        UpdateFileSearchCount();
        // Rows are on screen with letter fallbacks now; fill in real icons in the background
        // so the list never waits on shell icon extraction.
        QueueDeferredIconLoad(rows);
    }

    private async void LoadInitialFileResults(string terms, int generation, FileIndex index)
    {
        List<FileEntry> matches;
        try
        {
            matches = await Task.Run(() => terms.Length == 0
                ? index.Take(FileResultPageSize + 1)
                : index.Search(terms, FileResultPageSize + 1));
        }
        catch { return; }

        if (generation != _fileQueryGeneration || !IsOpen
            || !TryParseFileQuery(CurrentQuery, out var liveQuery)
            || !string.Equals(liveQuery, terms, StringComparison.Ordinal)) return;

        matches = KeepExistingFiles(matches);
        _fileResultsHasMore = matches.Count > FileResultPageSize;
        var rows = matches.Take(FileResultPageSize).Select(BuildFileRow).ToList();
        _fileResultsLoaded = rows.Count;
        _fileResultsSkip = rows.Count;
        var emptyText = rows.Count == 0 && terms.Length > 0
            ? $"没有找到「{terms}」"
            : rows.Count == 0 ? "索引中还没有可显示的项目" : string.Empty;
        _lastSlashSignature = null;
        ShowSlashRows(rows, emptyText);
        UpdateFileSearchChrome();
        UpdateFileSearchCount();
        QueueDeferredIconLoad(rows);
    }

    private void OnFileResultsScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        // Only a real scroll asks for the next page. Growing the panel (which changes the extent
        // and the viewport) must not chain-page the whole index in on its own.
        if (e.VerticalChange == 0) return;
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 72) return;

        var query = CurrentQuery;
        // The "跳转" second level is a fixed short list; nothing to page in.
        if (_jumpMenuQuery is not null) return;

        if (query.StartsWith('/'))
        {
            LoadNextFileResults();
            return;
        }

        // Plain query: the file section pages the same way, so pulling the list down keeps
        // bringing in the next batch instead of stopping at the first page.
        if (_plainFileQuery is { } plainQuery && PlainFileSearchActive(plainQuery))
            LoadPlainFilePage(plainQuery, replace: false);
    }

    private async void LoadNextFileResults()
    {
        if (!_fileResultsHasMore || _fileLoadingMore || App.Current.Files.IsBuilding
            || !CurrentSettings.FileSearchEnabled || _fileResultQuery is null) return;

        var generation = _fileQueryGeneration;
        var query = _fileResultQuery;
        var skip = _fileResultsSkip;
        _fileLoadingMore = true;
        List<FileEntry> matches;
        try
        {
            matches = await Task.Run(() => query.Length == 0
                ? App.Current.Files.Take(FileResultPageSize + 1, skip)
                : App.Current.Files.Search(query, FileResultPageSize + 1, skip));
        }
        catch
        {
            _fileLoadingMore = false;
            return;
        }

        if (generation != _fileQueryGeneration || !IsOpen
            || !TryParseFileQuery(CurrentQuery, out var liveQuery)
            || !string.Equals(liveQuery, query, StringComparison.Ordinal))
        {
            // A newer query owns the flag now; only the same generation may clear it.
            if (generation == _fileQueryGeneration) _fileLoadingMore = false;
            return;
        }

        _fileLoadingMore = false;
        matches = KeepExistingFiles(matches);
        _fileResultsHasMore = matches.Count > FileResultPageSize;

        // Offset paging can hand back a file that moved up the ranking and is already listed.
        var shown = new HashSet<string>(
            _tiles.OfType<ResultRow>().Where(r => r.Item is FileEntry)
                .Select(r => ((FileEntry)r.Item!).Path),
            StringComparer.OrdinalIgnoreCase);
        var page = matches.Take(FileResultPageSize).ToList();
        _fileResultsSkip = skip + page.Count;

        var next = page.Where(f => shown.Add(f.Path)).Select(BuildFileRow).ToList();
        foreach (var row in next) _tiles.Add(row);
        _fileResultsLoaded += next.Count;
        UpdateFileSearchCount();
        QueueDeferredIconLoad(next);
        ApplyPanelHeightNow();
        EnsureWindowFits();
    }

    private void UpdateFileSearchCount() =>
        ResultCount.Text = _fileResultsLoaded == 0
            ? string.Empty
            : _fileResultsHasMore ? $"{_fileResultsLoaded}+" : _fileResultsLoaded.ToString();

    /// <summary>
    /// Called (debounced) when an incremental index update lands, so an open panel showing file
    /// results keeps up with the disk instead of freezing on the rows it was built with.
    /// The refresh is done in place: rebuilding the list would drop every row (and the page the
    /// user already scrolled to) and read as a flash on the whole panel.
    /// </summary>
    public void RefreshFileResults()
    {
        if (!IsOpen) return;
        // The "跳转" picker is its own surface: an index tick must not splice file rows into it.
        if (_jumpMenuQuery is not null) return;

        var query = CurrentQuery;
        if (TryParseFileQuery(query, out var terms))
        {
            RefreshFileSearchInPlace(terms);
            return;
        }

        if (PlainFileSearchActive(query)) RefreshPlainFileResultsInPlace();
    }

    /// <summary>
    /// Re-reads as many "/s" rows as are on screen and merges them in, so rows of files that are
    /// still hits keep their identity and the scroll position survives.
    /// </summary>
    private async void RefreshFileSearchInPlace(string terms)
    {
        if (_fileLoadingMore || _fileResultsLoaded == 0)
        {
            if (_fileResultsLoaded == 0) RebuildFileSearch(terms); // nothing to preserve yet
            return;
        }
        if (!CurrentSettings.FileSearchEnabled) return;

        var index = App.Current.Files;
        if (index.IsBuilding || index.Count == 0) return;

        var want = _fileResultsLoaded;
        var generation = _fileQueryGeneration;
        _fileLoadingMore = true;

        List<FileEntry> matches;
        try
        {
            matches = await Task.Run(() => terms.Length == 0
                ? index.Take(want + 1)
                : index.Search(terms, want + 1));
        }
        catch
        {
            if (generation == _fileQueryGeneration) _fileLoadingMore = false;
            return;
        }

        if (generation != _fileQueryGeneration || !IsOpen
            || !TryParseFileQuery(CurrentQuery, out var liveQuery)
            || !string.Equals(liveQuery, terms, StringComparison.Ordinal))
        {
            if (generation == _fileQueryGeneration) _fileLoadingMore = false;
            return;
        }

        _fileLoadingMore = false;
        matches = KeepExistingFiles(matches);
        _fileResultsHasMore = matches.Count > want;

        var desired = new List<ResultRow>(want);
        var fresh = new List<ResultRow>();
        foreach (var file in matches.Take(want))
        {
            var row = _tiles.OfType<ResultRow>()
                .FirstOrDefault(r => SameIndexedFile(r, file));
            if (row is null) { row = BuildFileRow(file); fresh.Add(row); }
            desired.Add(row);
        }

        var unchanged = desired.Count == _tiles.Count;
        for (var i = 0; unchanged && i < desired.Count; i++)
            unchanged = ReferenceEquals(desired[i], _tiles[i]);

        _fileResultsLoaded = desired.Count;
        _fileResultsSkip = desired.Count;
        if (!unchanged)
        {
            SyncRange(_tiles, 0, desired.Cast<object>().ToList());
            _lastSlashSignature = null; // content moved: the next rebuild must resync
            ApplyPanelHeightNow();
        }
        UpdateFileSearchCount();
        if (fresh.Count > 0) QueueDeferredIconLoad(fresh);
    }

    private void UpdateFileSearchChrome()
    {
        if (RebuildIndexButton is null) return;
        var show = LocalFileSurfaceActive;
        RebuildIndexButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RebuildIndexButton.IsEnabled = show && !App.Current.Files.IsBuilding;
        RebuildIndexButton.ToolTip = App.Current.Files.IsBuilding
            ? "索引重建中…"
            : "手动重建本地索引（Ctrl+Enter）";
    }

    /// <summary>
    /// True when the current query reads the local index: "/s …" always, and a plain query when
    ///「普通搜索也检索本地文件」is on. Both surfaces offer the same manual rebuild button.
    /// </summary>
    private bool LocalFileSurfaceActive
    {
        get
        {
            var text = CurrentQuery;
            if (text.Length == 0 || !CurrentSettings.FileSearchEnabled) return false;
            if (TryParseFileQuery(text, out _)) return true;
            return !text.StartsWith('/') && CurrentSettings.FileSearchOnPlainQuery;
        }
    }

    private void OnRebuildIndexClick(object sender, RoutedEventArgs e) => RebuildFileIndexNow();

    /// <summary>
    /// Fills in real icons for the rows just shown, on a background STA thread. Indexing never
    /// touches icons and BuildFileRow only reads the cache, so the result list appears with
    /// letter fallbacks instantly; each extracted icon then lands on its row via INPC.
    /// </summary>
    private void QueueDeferredIconLoad(List<ResultRow> rows)
    {
        var pending = rows
            .Where(r => r.Item is FileEntry && r.Icon is null)
            .Select(r => (r, ((FileEntry)r.Item!).Path))
            .DistinctBy(r => r.Path)
            .Where(r => _iconLoadInFlight.Add(r.Path))
            .ToList();
        if (pending.Count == 0) return;

        var dispatcher = Dispatcher;
        var thread = new Thread(() =>
        {
            foreach (var (_, path) in pending)
            {
                ImageSource? icon = null;
                try
                {
                    icon = IconService.Get(path);
                }
                catch { /* a missing icon must never break the list */ }

                try
                {
                    var loadedIcon = icon;
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        _iconLoadInFlight.Remove(path);
                        if (loadedIcon is null) return;
                        // Update the visible row as soon as extraction finishes. If the result
                        // disappeared meanwhile, the successful icon remains in IconService's cache.
                        foreach (var live in _tiles.OfType<ResultRow>())
                            if (live.Item is FileEntry f && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
                            {
                                live.Icon = loadedIcon;
                                live.Glyph = null; // collapse the letter overlay after the image arrives
                            }
                    }));
                }
                catch { /* the window may be closing */ }
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private readonly HashSet<string> _iconLoadInFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Clear visible hits, rebuild the index, then rerun the active query. This is one of the
    /// only three ways a walk ever starts (the others are Ctrl+Enter and the settings ⟳).
    /// </summary>
    private void RebuildFileIndexNow()
    {
        if (!CurrentSettings.FileSearchEnabled || App.Current.Files.IsBuilding) return;
        // A plain query keeps its current results and refreshes once the walk is done; /s has
        // nothing useful to show meanwhile, so it swaps to the "rebuilding" line.
        var keepResults = !TryParseFileQuery(CurrentQuery, out _);

        _fileQueryGeneration++;
        _plainFileGeneration++;
        _fileLoadingMore = false;
        _fileResultsHasMore = false;
        _fileResultsLoaded = 0;
        _lastSlashSignature = null;
        App.Current.StartFileIndex(RefreshAfterIndexBuild);

        if (!keepResults)
        {
            ShowSlashRows(new List<ResultRow>(), "正在重建索引…");
            UpdateFileSearchCount();
        }
        UpdateFileSearchChrome();
    }

    private void RefreshAfterIndexBuild()
    {
        // The callback arrives on the worker thread; refresh the list once the user is
        // still looking at results that depend on the fresh index.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsOpen) return;
            var q = CurrentQuery;
            if (q.Length == 0) return;
            var fileMode = q.StartsWith('/') && TryParseFileQuery(q, out _);
            var plainMode = !q.StartsWith('/') && CurrentSettings.FileSearchOnPlainQuery;
            // In place, so the rows (and the page the user scrolled to) survive the rebuild.
            if (fileMode || plainMode) RefreshFileResults();
        }));
    }

    /// <summary>Shared chrome for both slash surfaces: one-line rows, no recent section.</summary>
    private void ShowSlashRows(List<ResultRow> rows, string emptyText)
    {
        // ResultRows are rebuilt on every keystroke, but Sync compares by reference. Without
        // this guard an unchanged /s list would replace every container and the panel would
        // flicker on each character — the exact thing we fixed for folder navigation.
        var signature = rows.Count == 0
            ? "empty:" + emptyText
            : string.Join("\u001f", rows.Select(r => r.Title + "\u001e" + r.Subtitle));
        if (signature == _lastSlashSignature)
        {
            Tiles.SelectedIndex = rows.Count > 0 ? 0 : -1;
            if (rows.Count > 0) LockHoverToPointer();
            return;
        }
        _lastSlashSignature = signature;

        _insertMarker = null;
        _markerIndex = -1;
        _recentItems = new List<ShortcutItem>();
        // A slash/file set is never a tile-grid set: force the next plain Rebuild() to re-sync
        // instead of short-circuiting on a stale result signature.
        _lastResultSignature = null;

        Sync(_tiles, rows.Cast<object>().ToList());
        Sync(_recentTiles, new List<object>());

        RecentSection.Visibility = Visibility.Collapsed;
        Scroller.Margin = new Thickness(14, 14, 10, 14);
        EmptyState.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;

        var none = rows.Count == 0;
        NoMatch.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Text = none ? emptyText : string.Empty;
        ResultCount.Text = rows.Count > 0 ? rows.Count.ToString() : string.Empty;
        Tiles.SelectedIndex = rows.Count > 0 ? 0 : -1;
        if (rows.Count > 0) LockHoverToPointer();

        var height = TargetPanelHeight();
        if (Math.Abs(Panel.ActualHeight - height) > 0.5)
            Panel.Set(System.Windows.Controls.Border.HeightProperty, height);
        EnsureWindowFits();
        if (Scroller.VerticalOffset > 0.5) Scroller.ScrollToTop();
    }

    private List<ResultRow> SlashRows(string query)
    {
        var body = query.TrimStart('/').Trim();
        var rows = new List<ResultRow>();

        if (body.Length == 0 || SlashSettings.StartsWith(body, StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(new ResultRow
            {
                Title = "打开设置",
                Subtitle = "/settings",
                Glyph = "⚙",
                Accent = Accent("#64748B"),
                Run = () => { Close(animated: true); App.Current.OpenSettings(); }
            });
        }

        if (body.Length == 0 || FileCommandAliases.Any(a => a.StartsWith(body, StringComparison.OrdinalIgnoreCase)))
        {
            rows.Add(new ResultRow
            {
                Title = "检索本地文件",
                Subtitle = "/s 关键词 · 检索本地文件与已安装应用",
                Glyph = "📁",
                Accent = Accent("#F59E0B"),
                Run = EnterFileMode
            });
        }

        return rows;
    }

    /// <summary>Choosing the /s hint just puts "/s " in the box and keeps the panel open.</summary>
    private void EnterFileMode()
    {
        Query.Text = "/s ";
        Query.CaretIndex = Query.Text.Length;
        FlushQueryDelay();
        Query.Focus();
    }

    /// <summary>
    /// A file found on disk: name plus the folder it lives in. The icon is cache-only here —
    /// a miss shows the letter fallback and the real icon arrives via the deferred load.
    /// </summary>
    private static ResultRow BuildFileRow(FileEntry file)
    {
        var isApp = FileIndex.IsAppEntry(file);
        var where = isApp
            ? "已安装应用"
            : file.IsDirectory ? $"文件夹 · {file.Folder}" : file.Folder;
        var when = isApp ? string.Empty : RelativeTime(file.WriteTime);
        var row = new ResultRow
        {
            Item = file,
            Title = file.Name,
            Subtitle = when.Length > 0 ? $"{when} · {where}" : where,
            Icon = IconService.TryGetCached(file.Path)
        };

        if (row.Icon is null && file.Name.Length > 0)
        {
            row.Glyph = file.Name[..1];
            row.Accent = isApp
                ? Accent("#0EA5E9")
                : file.IsDirectory ? Accent("#F59E0B") : Accent("#64748B");
        }

        return row;
    }

    private static string RelativeTime(DateTime time)
    {
        if (time <= DateTime.MinValue) return string.Empty;
        var span = DateTime.Now - time;
        if (span.TotalSeconds < 60) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
        return time.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// A list item of the result surface. Rows that are already list items (local files kept
    /// between rebuilds, the divider) are passed through untouched so their container survives;
    /// everything else is mapped onto its one-line row.
    /// </summary>
    private object? ToListItem(object item) => item switch
    {
        ResultRow row => row,
        DividerRow divider => divider,
        _ => ToResultRow(item)
    };

    /// <summary>Maps a shortcut / jump row onto the one-line list model.</summary>
    private ResultRow? ToResultRow(object item) => item switch
    {
        ShortcutItem shortcut => BuildItemRow(shortcut),
        FileEntry file => BuildFileRow(file),
        JumpEntry jump => new ResultRow
        {
            Item = jump,
            Title = jump.Name,
            Subtitle = $"「{jump.Query}」· {jump.Detail}",
            Glyph = "↗",
            Accent = Accent("#0EA5E9"),
            Run = () => OpenJumpMenu(jump.Query)
        },
        _ => null
    };

    /// <summary>An item row shows its real icon; without one it falls back to a lettered tile.</summary>
    private static ResultRow BuildItemRow(ShortcutItem item)
    {
        var row = new ResultRow
        {
            Item = item,
            Title = item.Name,
            Subtitle = RowSubtitle(item),
            Icon = item.Icon
        };

        if (item.Icon is null && item.Name.Length > 0)
        {
            row.Glyph = item.Name[..1];
            row.Accent = NameBrushConverter.Convert(item.Name, typeof(Brush), null!, CultureInfo.InvariantCulture) as Brush;
        }

        return row;
    }

    private static readonly Infrastructure.NameToBrushConverter NameBrushConverter = new();

    private static string RowSubtitle(ShortcutItem item)
    {
        var where = item.Folder is { Length: > 0 } folder ? $"分组 · {folder}" : item.Target;
        return item.IsUnavailable ? $"目标已失效 · {where}" : where ?? string.Empty;
    }

    private static SolidColorBrush Accent(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>Enter or click on a result row: run it, or open the shortcut it stands for.</summary>
    private void RunResultRow(ResultRow row)
    {
        if (row.Run is { } run) { run(); return; }
        if (row.Item is { } item) { Invoke(item, null); return; }
        ShowMessage("这一行只是用法提示");
    }
}
