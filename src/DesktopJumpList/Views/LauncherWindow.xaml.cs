 ﻿using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;
using DesktopJumpList.Services;
using Microsoft.Win32;

namespace DesktopJumpList.Views;

/// <summary>Sentinel item for the "+" tile.</summary>
public sealed class AddTile
{
    public static readonly AddTile Instance = new();
    private AddTile() { }

    public string Name => "添加";
}

/// <summary>A named group of shortcuts (iOS-style folder) shown as one tile.</summary>
public sealed class FolderTile
{
    public string Name { get; }
    public IReadOnlyList<ShortcutItem> Members { get; }
    public IReadOnlyList<ShortcutItem> Preview { get; }

    public FolderTile(string name, IReadOnlyList<ShortcutItem> members)
    {
        Name = name;
        Members = members;
        Preview = members.Take(4).ToList();
    }

    public string Tooltip => $"{Name}\n分组 · {Members.Count} 项\n单击打开，右键重命名或解散";
}

/// <summary>Sentinel item for the "back to all" tile inside a folder view.</summary>
public sealed class BackTile
{
    public string FolderName { get; }
    public BackTile(string folderName) => FolderName = folderName;
    public string Name => "全部";
}

/// <summary>Placeholder shown while dragging: the cell opens up where the tile will land.</summary>
public sealed class InsertMarker
{
    public static readonly InsertMarker Instance = new();
    private InsertMarker() { }
    public string Name => string.Empty;
}

/// <summary>Sentinel item for the last cell of the one-row "recently used" header.</summary>
public sealed class MoreRecentTile
{
    public static readonly MoreRecentTile Instance = new();
    private MoreRecentTile() { }
    public string Name => "更多";
}

/// <summary>
/// Divider between the panel's own matches and the local-file hits of a plain query. It is a
/// list item so it scrolls with the results, but it can never be selected or clicked.
/// </summary>
public sealed class DividerRow
{
    public DividerRow(string title) => Title = title;
    public string Title { get; }
}

/// <summary>
/// The centred panel. Nothing but your shortcuts: a grid of tiles, type-to-filter, click to open.
/// The search pill only exists while you are typing (or for a brief message).
/// </summary>
public partial class LauncherWindow : Window
{
    private const double PanelChrome = 2 + 28; // border + inner padding
    private const double EmptyHeight = 210;
    private const double CanvasMargin = 36;
    private const double PillRowHeight = 50; // 40 px pill + 10 px gap
    private const double IconTopPadding = 8;
    private const double TileBottomPadding = 8;
    private const double PanelSidePadding = 26; // 14 left + 10 right + 2 border
    private const double LabelWidth = 88;       // wrap width of a tile label
    private const double RowPaddingV = 9;       // vertical padding of a one-line result row

    /// <summary>
    /// One-line result row (icon + two texts), derived from the panel settings so the search
    /// surface follows the same icon size / font size / gaps as the tile grid.
    /// </summary>
    private double RowTitleLine => Math.Round(TileFontSize * 1.35);
    private double RowSubLine => Math.Round(Math.Max(9, TileFontSize - 2) * 1.3) + 1;
    private double RowHeight => Math.Round(
        Math.Max(TileIconSize, RowTitleLine + RowSubLine) + RowPaddingV * 2);
    /// <summary>Vertical pitch of one list row: the row plus the item gap below it.</summary>
    private double RowPitch => RowHeight + ItemGap;

    private static AppSettings CurrentSettings => App.Current.Settings;

    private double TileIconSize => Math.Max(0, CurrentSettings.PanelIconSize);
    private double TileFontSize => Math.Max(0, CurrentSettings.PanelFontSize);
    private double TileGap => Math.Max(0, CurrentSettings.PanelGap);
    private double LabelLineHeight => Math.Round(TileFontSize * 1.33);
    private double LabelArea => LabelLineHeight * 2;
    private double TileHeight => IconTopPadding + TileIconSize + TileGap + LabelArea + TileBottomPadding;

    /// <summary>Gap between tiles (both axes) — the panel grows with it.</summary>
    private double ItemGap => Math.Max(0, CurrentSettings.PanelItemGap);

    /// <summary>A tile is as wide as its widest part (label or icon), plus a little breathing room.</summary>
    private double TileWidth => Math.Round(Math.Max(LabelWidth, TileIconSize) + 12);

    /// <summary>One grid cell: the tile plus the gap that follows it.</summary>
    private double CellWidth => TileWidth + ItemGap;
    private double CellHeight => TileHeight + ItemGap;

    /// <summary>Width follows the columns, so five columns of 96 px tiles make a narrower panel than five of 120 px.</summary>
    private double PanelWidth => Math.Round(Cols * CellWidth + PanelSidePadding);

    /// <summary>Tallest the panel may get on the current screen, leaving room for the pill and the shadow.</summary>
    private double MaxPanelHeight
    {
        get
        {
            var area = ScreenGeometry.WorkAreaAtCursor(this);
            return Math.Max(180, area.Height - CanvasMargin * 2 - PillRowHeight - 24);
        }
    }

    private int Cols => Math.Max(1, App.Current.Settings.Columns);
    private int MaxRowCount => Math.Max(1, App.Current.Settings.MaxRows);

    private readonly ShortcutLibrary _library;
    private readonly ObservableCollection<object> _tiles = new();
    private readonly ObservableCollection<object> _recentTiles = new();
    private readonly DispatcherTimer _messageTimer;
    private DispatcherTimer _queryDelay = null!;
    private string? _lastResultSignature;
    private bool _debounceEnabled = true;
    private readonly DispatcherTimer _dragScroll;
    private InsertMarker? _insertMarker;
    private int _markerIndex = -1;
    private double _dragScrollStep;
    private Point _lastDragPoint;
    private bool _draggingTile;
    private bool _listMode; // search results / palette use a one-line list instead of the tile grid
    private bool _externalFileDrag; // an OLE file drag from another app is in progress: don't auto-close

    // Anchor of the pointer's last real movement — see OnTilesMouseMove.
    private Point _hoverAnchor;
    private bool _hoverAnchorValid;
    // True while keyboard navigation owns the selection: hover may only reclaim it after a
    // deliberate, larger pointer move (small tremor or a scroll-synthesised event must not).
    private bool _hoverKeyboardLock;

    private List<ShortcutItem> _ordered = new();
    private List<ShortcutItem> _recentItems = new();
    private string? _currentFolder;
    private bool _recentView;
    private bool _closing;
    private int _modalDepth;
    private ShortcutItem? _renaming;
    private (ShortcutItem item, int index)? _lastRemoved;
    private Action? _messageAction;
    private string? _pendingNotice;

    // Long-press → edit mode, and edit-mode drag state.
    private DispatcherTimer? _longPress;
    private bool _editMode;
    private bool _longPressTriggered;
    private bool _dragArmed;
    private Point _tilePressPoint;
    private object? _tilePressData;
    private FrameworkElement? _tilePressContainer;

    public bool InEditMode => _editMode;

    public bool IsOpen { get; private set; }


    /// <summary>True while a file/folder/store-app dialog owns the UI.</summary>
    public bool IsModal => _modalDepth > 0;

    public LauncherWindow(ShortcutLibrary library)
    {
        InitializeComponent();
        _library = library;
        Tiles.ItemsSource = _tiles;
        RecentTiles.ItemsSource = _recentTiles;

        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _messageTimer.Tick += (_, _) => HideMessage();

        // Dragging a tile near the top/bottom edge scrolls the list, otherwise a tile can only be
        // moved inside the rows that happen to be visible.
        _dragScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _dragScroll.Tick += (_, _) =>
        {
            if (Math.Abs(_dragScrollStep) < 0.5) return;
            var before = Scroller.VerticalOffset;
            Scroller.ScrollToVerticalOffset(before + _dragScrollStep);
            if (Math.Abs(Scroller.VerticalOffset - before) < 0.5) return;
            UpdateInsertMarkerAt(_lastDragPoint);
        };

        _longPress = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _longPress.Tick += (_, _) =>
        {
            _longPress.Stop();
            if (_tilePressData is ShortcutItem or FolderTile)
            {
                _longPressTriggered = true;
                EnterEditMode();
                _dragArmed = true; // keep the press alive: the same gesture can continue into a drag
            }
        };

        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, noActivate: false);
        new WindowInteropHelper(this).EnsureHandle(); // first open is as fast as every other

        // Alt+F4 (or any shell close request) must hide the panel, never destroy the window;
        // the app is tray-resident and reopening a closed WPF Window would throw.
        Closing += (_, e) =>
        {
            if (App.Current.IsShuttingDown) return;
            e.Cancel = true;
            Close(animated: true);
        };

        Deactivated += (_, _) => { if (IsOpen && _modalDepth == 0 && !_externalFileDrag) Close(animated: true); };
        PreviewKeyDown += OnKey;
        Scroller.ScrollChanged += OnFileResultsScrollChanged;
        // Typing should not rebuild the list on every keystroke: wait until the input pauses,
        // and let navigation keys flush the pending query so the first arrow press is accurate.
        _queryDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _queryDelay.Tick += (_, _) => { _queryDelay.Stop(); OnQueryChanged(); };
        ApplySearchDebounce();
        Query.TextChanged += (_, _) =>
        {
            // Show the search box immediately — only the list rebuild is allowed to wait.
            UpdatePill();
            UpdateFileSearchChrome();
            RestartQueryDelay();
        };

        Tiles.PreviewMouseMove += OnTilesMouseMove;
        Tiles.AddHandler(ListBoxItem.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnTileDown), true);
        Tiles.AddHandler(ListBoxItem.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnTileUp), true);
        Tiles.AddHandler(ListBoxItem.MouseRightButtonUpEvent, new MouseButtonEventHandler(OnTileRightClick), true);

        RecentTiles.PreviewMouseLeftButtonUp += OnRecentTileUp;
        RecentTiles.PreviewMouseRightButtonUp += OnTileRightClick;

        EmptyState.MouseLeftButtonUp += (_, _) => ShowAddMenu(null);
        MessageAction.Click += (_, _) => { var a = _messageAction; HideMessage(); a?.Invoke(); };
        RenameBox.PreviewKeyDown += OnRenameKey;

        WheelScroller.Attach(this, () => Scroller);

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        QueryContinueDrag += (_, e) => { if (e.Action == DragAction.Cancel) EndInternalDrag(); };

        _library.Items.CollectionChanged += OnLibraryChanged;

        ApplyPanelMetrics();
        ApplyLayout(); // honour the configured columns/rows before first open
    }

    // ───────────────────────── Open / close ─────────────────────────

    public void Open()
    {
        if (IsOpen && !_closing)
        {
            Activate();
            return;
        }

        var wasClosing = _closing;
        _closing = false;
        IsOpen = true;

        if (!wasClosing)
        {
            _editMode = false;

            Query.Text = string.Empty;
            HideMessage(immediate: true);
            HideRename(immediate: true);
            _currentFolder = null;
            _recentView = false;
            _ordered = _library.Ordered();
            // The disk index is deliberately NOT built here. It is only needed by the "/s"
            // command, so walking the user's folders on every panel open would be pure cost.
            Rebuild();
            var panelHeight = TargetPanelHeight();
            Panel.Set(HeightProperty, panelHeight);
            Scroller.ScrollToTop();

            // The window is a transparent canvas around the panel: it has to follow the panel's
            // real size, otherwise a tall panel loses its rounded bottom corners.
            ResizeWindow();

            // Top-anchored stack: the search pill and panel top stay put while the result
            // count (and therefore the panel height) changes. The window itself is placed
            // so the initial panel is optically centred on the monitor under the pointer.
            var wa = ScreenGeometry.WorkAreaAtCursor(this);
            Left = wa.Left + (wa.Width - Width) / 2;
            var desiredTop = wa.Top + wa.Height / 2 - CanvasMargin - PillRowHeight - panelHeight / 2 - 16;
            Top = Math.Clamp(desiredTop, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));

            Stack.Set(OpacityProperty, 0);
            StackScale.Set(ScaleTransform.ScaleXProperty, 1);
            StackScale.Set(ScaleTransform.ScaleYProperty, 1);
            StackShift.Set(TranslateTransform.YProperty, 12);
        }

        // A layered window that is shown again hands the compositor the surface it had the last
        // time it was visible, so the first frames were a stale slice of the old content. The
        // window is therefore fully transparent while it is shown and laid out, and becomes
        // visible in the same dispatcher turn — the compositor never sees the stale frame.
        Opacity = 0;
        Show();
        UpdateLayout();
        Opacity = 1;

        // A cursor that sits still over the list must not claim the selection on the first
        // synthesized mouse-move: seed the hover anchor with where the pointer is right now.
        _hoverAnchor = Mouse.GetPosition(this);
        _hoverAnchorValid = true;

        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Query.Focus();
        Keyboard.Focus(Query);

        // A short rise and a fade — no scaling and no per-tile stagger, which together read as a
        // "jelly" wobble on every icon while the panel opened.
        Stack.To(OpacityProperty, 1, 180);
        StackShift.To(TranslateTransform.YProperty, 0, 200, Motion.Out);

        if (!wasClosing && _pendingNotice is { } notice)
        {
            _pendingNotice = null;
            Dispatcher.BeginInvoke(() => ShowMessage(notice), DispatcherPriority.Input);
        }
    }

    public void Close(bool animated)
    {
        if (!IsOpen || _closing) return;
        _closing = true;

        void Finish()
        {
            if (!_closing) return; // re-opened mid-fade
            _closing = false;
            IsOpen = false;
            _longPress?.Stop();
            _dragArmed = false;
            _editMode = false;
            EndInternalDrag();
            EditHintBar.To(OpacityProperty, 0, 0);
            EditHintBar.Visibility = Visibility.Collapsed;

            Hide();
            Query.Text = string.Empty;
            Tiles.SelectedIndex = -1;
            _hoverAnchorValid = false;
            HideRename(immediate: true);
            HideMessage(immediate: true);
            SetDropOverlay(false);
            Panel.Set(HeightProperty, TargetPanelHeight());
            App.Current.OnLauncherClosed();
        }

        if (!animated) { Finish(); return; }

        Stack.To(OpacityProperty, 0, 130, Motion.In, completed: Finish);
        StackShift.To(TranslateTransform.YProperty, 6, 130, Motion.In);
    }

    // ───────────────────────── Tiles ─────────────────────────

    private string CurrentQuery => Query.Text.Trim();

    private void Rebuild()
    {
        var q = CurrentQuery;

        // The second level of a "跳转" row belongs to the query it was opened for: any edit
        // drops back to the plain result list.
        if (_jumpMenuQuery is not null && !string.Equals(_jumpMenuQuery, q, StringComparison.Ordinal))
            _jumpMenuQuery = null;

        SetListMode(q.Length > 0);
        if (q.StartsWith('/'))
        {
            // "/s ..." is its own mode: results contain local files and nothing else.
            // Everything else starting with "/" stays the small command palette.
            _jumpMenuQuery = null;
            DropPlainFileResults();
            if (TryParseFileQuery(q, out var fileTerms)) RebuildFileSearch(fileTerms);
            else RebuildSlash(q);
            return;
        }
        // Leaving a slash surface invalidates its "nothing changed" shortcut.
        _lastSlashSignature = null;

        if (_jumpMenuQuery is not null) { RebuildJumpMenu(q); return; }

        var items = q.Length == 0 ? _ordered : ShortcutLibrary.Search(_ordered, q);

        _insertMarker = null;
        _markerIndex = -1;
        _recentItems = new List<ShortcutItem>();

        // Build the target list first, then merge it into the bound collection item by item.
        // Clearing and refilling re-creates every container, which made the whole grid flash
        // whenever a folder was opened or a character was typed.
        var next = new List<object>();

        if (q.Length == 0 && _recentView)
        {
            // The "more" view: back tile + every retained recently-used entry.
            _recentItems = RecentItems();
            next.Add(new BackTile("全部"));
            foreach (var item in _recentItems) next.Add(item);
        }
        else if (q.Length == 0 && _currentFolder is not null)
        {
            // Inside a folder: the folder's contents take over the main grid, so the only thing
            // that changes on screen is the grid itself — the recently-used row above stays put.
            next.Add(new BackTile(_currentFolder));
            foreach (var i in _ordered.Where(x => SameFolder(x.Folder, _currentFolder))) next.Add(i);
            next.Add(AddTile.Instance);
        }
        else
        {
            // Top level (or a search, which flattens folders so everything is findable).
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var item in items)
            {
                if (item.Folder is null)
                {
                    next.Add(item);
                    continue;
                }
                if (q.Length == 0 && seen.Add(item.Folder))
                {
                    var members = items.Where(x => SameFolder(x.Folder, item.Folder)).ToList();
                    next.Add(new FolderTile(item.Folder, members));
                }
                else if (q.Length > 0)
                {
                    next.Add(item); // search: show the item itself, not the folder
                }
            }
            if (q.Length == 0 && _ordered.Count > 0) next.Add(AddTile.Instance);

            // Typing also offers the web, through ONE "跳转" row: the engines live one level
            // deeper, so the list is never padded with a row per engine.
            if (q.Length > 0)
            {
                var engines = SearchEngineCatalog
                    .Resolve(CurrentSettings.SearchJumpRules ?? new List<SearchJumpRule>())
                    .ToList();
                if (engines.Count > 0)
                {
                    // Reuse the entry while the query is unchanged: the result signature is built
                    // from object identity, so a fresh instance would resync the whole list.
                    if (_jumpEntry is null || !string.Equals(_jumpEntry.Query, q, StringComparison.Ordinal)
                        || _jumpEntry.TargetCount != engines.Count)
                        _jumpEntry = new JumpEntry(q, engines.Count);
                    next.Add(_jumpEntry);
                }
                else _jumpEntry = null;
            }
        }

        // Plain query with「普通搜索也检索本地文件」on: the index hits stay below a divider, so the
        // panel's own matches always keep the top of the list. The rows are kept in
        // _plainFileRows between rebuilds — re-creating them on every keystroke (or on every
        // index tick) would make the whole section blink.
        var plainFiles = q.Length > 0 && PlainFileSearchActive(q);
        if (plainFiles)
        {
            if (!string.Equals(_plainFileQuery, q, StringComparison.Ordinal)) ResetPlainFileResults(q);
            if (_plainFileRows.Count > 0)
            {
                _plainFileDivider ??= new DividerRow("本地文件");
                next.Add(_plainFileDivider);
                next.AddRange(_plainFileRows);
            }
        }
        else if (_plainFileQuery is not null) DropPlainFileResults();

        // The tile list can remain unchanged while layout settings alter the recent row, so only
        // skip syncing the main list here; continue through the recent-row and visibility updates.
        var signature = $"{_listMode}:{Signature(next)}";
        var resultsChanged = signature != _lastResultSignature;
        if (resultsChanged)
        {
            _lastResultSignature = signature;
            if (_listMode) next = next.Select(ToListItem).OfType<object>().ToList();
            Sync(_tiles, next);
        }

        // One-row recently-used header above the divider. It keeps showing inside a folder, so
        // opening one only ever swaps the grid below it.
        var nextRecent = new List<object>();
        if (q.Length == 0 && !_recentView)
        {
            _recentItems = RecentItems();
            if (_recentItems.Count > 0)
            {
                var hasMore = _recentItems.Count > Cols;
                var shown = _recentItems.Take(Math.Max(0, Cols - (hasMore ? 1 : 0))).ToList();
                foreach (var item in shown) nextRecent.Add(item);
                if (hasMore) nextRecent.Add(MoreRecentTile.Instance);
            }
        }
        Sync(_recentTiles, nextRecent);

        var recentVisible = _recentTiles.Count > 0;
        RecentSection.Visibility = recentVisible ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Margin = recentVisible
            ? new Thickness(14, 0, 10, 14)
            : new Thickness(14, 14, 10, 14);

        bool empty = _ordered.Count == 0;
        EmptyState.Visibility = empty && q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = EmptyState.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        bool noMatch = q.Length > 0 && next.Count == 0;
        NoMatch.Visibility = noMatch ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Text = noMatch ? $"没有找到 “{q}”" : string.Empty;
        ResultCount.Text = q.Length > 0 && items.Count > 0 ? items.Count.ToString() : string.Empty;

        if (q.Length > 0)
        {
            Tiles.SelectedIndex = next.Count > 0 ? 0 : -1;
            if (next.Count > 0) LockHoverToPointer(); // fresh results start on row 1, not on a stale hover
        }
        else
        {
            // 主面板：默认选中最近使用的第一个；没有最近使用时选中主面板第一个磁贴。
            if (recentVisible && RecentTiles.Items.Count > 0)
            {
                RecentTiles.SelectedIndex = 0;
                Tiles.SelectedIndex = -1;
            }
            else if (Tiles.Items.Count > 0)
            {
                Tiles.SelectedIndex = 0;
                RecentTiles.SelectedIndex = -1;
            }
            else
            {
                Tiles.SelectedIndex = -1;
                RecentTiles.SelectedIndex = -1;
            }
        }

        if (q.Length > 0 && _plainFileRows.Count > 0) UpdatePlainFileCount();
    }

    // ───────────────── Plain query → local files ─────────────────

    /// <summary>Rows a plain query adds per page, and again on every scroll to the bottom.</summary>
    private const int PlainFilePageSize = 20;

    /// <summary>
    /// The local-file hits of a plain query, kept across rebuilds. They are the same ResultRow
    /// objects for as long as the file is still a hit, so the list container is reused and the
    /// section never blinks when the index ticks or a page is appended.
    /// </summary>
    private readonly List<ResultRow> _plainFileRows = new();
    private DividerRow? _plainFileDivider;
    private string? _plainFileQuery;
    /// <summary>How many ranked hits the next page starts after (not how many rows are shown).</summary>
    private int _plainFileSkip;
    private bool _plainFileHasMore;
    private bool _plainFileLoading;
    private int _plainFileGeneration;

    /// <summary>Pages pulled automatically because the loaded ones fit on screen (bounded).</summary>
    private int _plainAutoPages;

    /// <summary>True when a plain query should also read the local index.</summary>
    private static bool PlainFileSearchActive(string query) =>
        query.Length > 0 && !query.StartsWith('/')
        && CurrentSettings.FileSearchEnabled && CurrentSettings.FileSearchOnPlainQuery;

    /// <summary>Forgets the file section of the previous query (rows disappear next rebuild).</summary>
    private void DropPlainFileResults()
    {
        _plainFileGeneration++;
        // The in-flight page of the old query is now orphaned: its result is discarded by the
        // generation check, so the loading flag must not block the new query's own search.
        // Typing cancels the running search instead of queueing behind it.
        _plainFileLoading = false;
        _plainFileQuery = null;
        _plainFileDivider = null;
        _plainFileRows.Clear();
        _plainFileSkip = 0;
        _plainFileHasMore = false;
        _plainFileLoading = false;
        _plainAutoPages = 0;
    }

    private void ResetPlainFileResults(string query)
    {
        DropPlainFileResults();
        _plainFileQuery = query;
        _plainFileDivider = new DividerRow("本地文件");
        LoadPlainFilePage(query, replace: true);
    }

    /// <summary>
    /// Fetches one page of local-file hits for a plain query, off the UI thread (the index search
    /// walks every entry). <paramref name="replace"/> reloads from the first hit; otherwise the
    /// page continues after the rows already on screen.
    /// </summary>
    private async void LoadPlainFilePage(string query, bool replace)
    {
        if (_plainFileLoading) return;
        if (!replace && !_plainFileHasMore) return;
        if (!PlainFileSearchActive(query)) return;

        var index = App.Current.Files;
        if (index.IsBuilding || index.Count == 0) return;

        var generation = _plainFileGeneration;
        var skip = replace ? 0 : _plainFileSkip;
        _plainFileLoading = true;

        List<FileEntry> matches;
        try { matches = await Task.Run(() => index.Search(query, PlainFilePageSize + 1, skip)); }
        catch
        {
            if (generation == _plainFileGeneration) _plainFileLoading = false;
            return;
        }

        if (generation != _plainFileGeneration || !IsOpen
            || !string.Equals(CurrentQuery, query, StringComparison.Ordinal)
            || !PlainFileSearchActive(query)) return;

        _plainFileLoading = false;
        matches = KeepExistingFiles(matches);
        _plainFileHasMore = matches.Count > PlainFilePageSize;

        if (replace) _plainFileRows.Clear();

        var page = matches.Take(PlainFilePageSize).ToList();
        // The offset advances by what the index handed out, not by what is new on screen: a hit
        // that moved up the ranking is already listed and must not shift the next page.
        _plainFileSkip = skip + page.Count;
        if (page.Count > 0) AppendPlainFileRows(page);
        else NoMatch.Visibility = _tiles.Count == 0 ? Visibility.Visible : NoMatch.Visibility;
    }

    /// <summary>Adds entries to the file section, reusing the row of a file that is already there.</summary>
    private void AppendPlainFileRows(List<FileEntry> files)
    {
        // Paging is offset-based, so a file that moved up the ranking can be offered twice.
        var seen = new HashSet<string>(
            _plainFileRows.Where(r => r.Item is FileEntry).Select(r => ((FileEntry)r.Item!).Path),
            StringComparer.OrdinalIgnoreCase);

        var rows = new List<ResultRow>(files.Count);
        foreach (var file in files)
        {
            if (!seen.Add(file.Path)) continue;
            rows.Add(BuildFileRow(file));
        }
        if (rows.Count == 0) return;

        _plainFileRows.AddRange(rows);

        // While the "跳转" picker owns the list the rows are only recorded here; the next
        // Rebuild puts them back under their divider.
        if (_jumpMenuQuery is not null) return;

        ApplyPlainFileTail();

        if (rows.Count > 0)
        {
            NoMatch.Visibility = Visibility.Collapsed;
            // The very first hits of a query must not steal the selection from the query's own
            // matches; only a selection that already sits inside the file section moves on.
            if (Tiles.SelectedIndex < 0 || _tiles[Tiles.SelectedIndex] is DividerRow)
                Tiles.SelectedIndex = _tiles.IndexOf(rows[0]);
        }
        UpdatePlainFileCount();

        ApplyPanelHeightNow();
        EnsureWindowFits();
        QueueDeferredIconLoad(rows);

        // A page that fits on screen leaves nothing to scroll, so "pull down for more" would be
        // unreachable. Pull the next page proactively until the list overflows (bounded: the
        // panel height is capped, so this always terminates in a couple of pages).
        if (_plainFileHasMore && _plainAutoPages < 4)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsOpen || _jumpMenuQuery is not null) return;
                if (_plainFileQuery is not { } live || !_plainFileHasMore) return;
                if (Scroller.ExtentHeight > Scroller.ViewportHeight + 1) return;
                _plainAutoPages++;
                LoadPlainFilePage(live, replace: false);
            }), DispatcherPriority.Background);
        }
    }

    /// <summary>Re-syncs the rows after the divider, touching only what actually changed.</summary>
    private void ApplyPlainFileTail()
    {
        if (_plainFileDivider is null) return;
        var divider = _tiles.IndexOf(_plainFileDivider);
        if (_plainFileRows.Count == 0)
        {
            if (divider >= 0) while (_tiles.Count > divider) _tiles.RemoveAt(_tiles.Count - 1);
            return;
        }

        if (divider < 0)
        {
            // The panel's own matches are already on screen (the divider only arrives with the
            // first hit), so appending is exactly the right place for the section.
            _tiles.Add(_plainFileDivider);
            divider = _tiles.Count - 1;
        }

        SyncRange(_tiles, divider + 1, _plainFileRows.Cast<object>().ToList());
    }

    private static bool SameIndexedFile(ResultRow row, FileEntry file) =>
        row.Item is FileEntry existing && string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>"12" while the list is complete, "12+" when a scroll still has more to load.</summary>
    private void UpdatePlainFileCount()
    {
        var shown = _tiles.Count(item => item is not DividerRow);
        ResultCount.Text = _plainFileHasMore ? $"{shown}+" : shown.ToString();
    }

    /// <summary>
    /// Re-reads the file section in place after an incremental index update. Rows of files that
    /// are still hits keep their identity, so the section does not blink on every USN batch.
    /// </summary>
    private async void RefreshPlainFileResultsInPlace()
    {
        if (_plainFileQuery is not { } query || _plainFileLoading) return;
        if (!PlainFileSearchActive(query) || !IsOpen) return;

        var index = App.Current.Files;
        if (index.IsBuilding || index.Count == 0) return;

        var want = Math.Max(PlainFilePageSize, _plainFileRows.Count);
        var generation = _plainFileGeneration;
        _plainFileLoading = true;

        List<FileEntry> matches;
        try { matches = await Task.Run(() => index.Search(query, want + 1, 0)); }
        catch
        {
            if (generation == _plainFileGeneration) _plainFileLoading = false;
            return;
        }

        if (generation != _plainFileGeneration || !IsOpen
            || !string.Equals(CurrentQuery, query, StringComparison.Ordinal)) return;

        _plainFileLoading = false;
        matches = KeepExistingFiles(matches);
        _plainFileHasMore = matches.Count > want;

        var desired = new List<ResultRow>(want);
        var fresh = new List<ResultRow>();
        foreach (var file in matches.Take(want))
        {
            var row = _plainFileRows.FirstOrDefault(r => SameIndexedFile(r, file));
            if (row is null) { row = BuildFileRow(file); fresh.Add(row); }
            desired.Add(row);
        }

        var unchanged = desired.Count == _plainFileRows.Count;
        for (var i = 0; unchanged && i < desired.Count; i++)
            unchanged = ReferenceEquals(desired[i], _plainFileRows[i]);
        if (unchanged) return; // nothing to repaint at all

        _plainFileRows.Clear();
        _plainFileRows.AddRange(desired);
        _plainFileSkip = desired.Count;
        ApplyPlainFileTail();
        UpdatePlainFileCount();
        if (fresh.Count > 0) QueueDeferredIconLoad(fresh);
        ApplyPanelHeightNow();
    }

    /// <summary>
    /// Drops local-file rows whose path is gone. The index can lag behind the disk (the app was
    /// closed, or the watcher buffer overflowed), and showing a file that no longer exists is
    /// worse than a slightly shorter list — the stale rows are also evicted from the index.
    /// </summary>
    private List<FileEntry> KeepExistingFiles(List<FileEntry> files)
    {
        if (files.Count == 0) return files;

        var alive = new List<FileEntry>(files.Count);
        var gone = new List<string>();
        foreach (var file in files)
        {
            // Store apps are virtual entries (shell:AppsFolder\…): they have no path on disk.
            if (FileIndex.IsAppEntry(file) || ExistsOnDisk(file.Path)) alive.Add(file);
            else gone.Add(file.Path);
        }

        if (gone.Count > 0) App.Current.Files.RemovePaths(gone, notify: false);
        return alive;
    }

    /// <summary>True unless the path is definitely gone; access errors keep the row.</summary>
    private static bool ExistsOnDisk(string path)
    {
        try { System.IO.File.GetAttributes(path); return true; }
        catch (System.IO.FileNotFoundException) { return false; }
        catch (System.IO.DirectoryNotFoundException) { return false; }
        catch { return true; }
    }

    /// <summary>Identity of a result set: reference hashes of the items, in order.</summary>
    private static string Signature(List<object> items)
    {
        if (items.Count == 0) return "0";
        var builder = new System.Text.StringBuilder(items.Count * 9);
        foreach (var item in items) builder.Append(RuntimeHelpers.GetHashCode(item).ToString("x")).Append(',');
        return builder.ToString();
    }

    /// <summary>
    /// Turns <paramref name="target"/> into <paramref name="items"/> with the fewest possible
    /// collection operations, so containers that survive keep their visuals (and nothing outside
    /// the changed cells is re-rendered). Two phases: drop what is gone, then order what is left.
    /// </summary>
    private static void Sync(ObservableCollection<object> target, List<object> items)
    {
        var keep = new HashSet<object>(items, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);

        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], items[i])) continue;

            var found = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (!ReferenceEquals(target[j], items[i])) continue;
                found = j;
                break;
            }

            if (found >= 0) target.Move(found, i);
            else target.Insert(i, items[i]);
        }
    }

    /// <summary>
    /// The same minimal-op merge as <see cref="Sync"/>, but only for the tail that starts at
    /// <paramref name="start"/> — used by the local-file section under its divider, so the
    /// query's own rows above it are never touched.
    /// </summary>
    private static void SyncRange(ObservableCollection<object> target, int start, List<object> items)
    {
        for (var i = target.Count - 1; i >= start + items.Count; i--) target.RemoveAt(i);

        for (var i = 0; i < items.Count; i++)
        {
            var at = start + i;
            if (at < target.Count && ReferenceEquals(target[at], items[i])) continue;

            var found = -1;
            for (var j = at; j < target.Count; j++)
            {
                if (!ReferenceEquals(target[j], items[i])) continue;
                found = j;
                break;
            }

            if (found >= 0) target.Move(found, at);
            else target.Insert(at, items[i]);
        }
    }

    private List<ShortcutItem> RecentItems()
    {
        var take = Math.Max(0, CurrentSettings.RecentCount);
        return _library.Items
            .Where(i => i.LastOpenedAt is not null)
            .OrderByDescending(i => i.LastOpenedAt)
            .ThenByDescending(i => i.OpenCount)
            .Take(take)
            .ToList();
    }

    private static bool SameFolder(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

    private void ApplyPanelMetrics()
    {
        Resources["TileHeightValue"] = TileHeight;
        Resources["IconSizeValue"] = TileIconSize;
        Resources["LabelFontSizeValue"] = TileFontSize;
        Resources["LabelLineHeightValue"] = LabelLineHeight;
        Resources["LabelMaxHeightValue"] = LabelArea;
        Resources["LabelMarginValue"] = new Thickness(0, TileGap, 0, 0);
        Resources["LetterFontSizeValue"] = Math.Round(TileIconSize * 0.42);
        Resources["TileWidthValue"] = TileWidth;
        // Right+bottom gap on each cell: the row/column pitch becomes tile + gap, so the panel can
        // be sized exactly as columns*cell and nothing gets squeezed.
        Resources["TileMarginValue"] = new Thickness(0, 0, ItemGap, ItemGap);
        Resources["RecentRowHeightValue"] = CellHeight;
        // Result rows follow the same icon size / font size / gaps as the tiles.
        Resources["RowIconValue"] = TileIconSize;
        Resources["RowTitleFontSizeValue"] = TileFontSize;
        Resources["RowSubFontSizeValue"] = Math.Max(9, TileFontSize - 2);
        Resources["RowLetterFontSizeValue"] = Math.Round(TileIconSize * 0.42);
        Resources["RowTextMarginValue"] = new Thickness(TileGap, 0, 12, 0);
        Resources["RowHeightValue"] = RowHeight;
        Resources["RowItemMarginValue"] = new Thickness(0, 0, 0, ItemGap);
        Panel.Width = PanelWidth;
        // Search pill: user width, hard-capped at the panel width.
        Pill.Width = Math.Clamp(CurrentSettings.SearchBoxWidth, 240, (int)PanelWidth);
    }

    /// <summary>Applies the search pill's visibility/width settings.</summary>
    public void ApplySearchBoxSettings()
    {
        ApplyPanelMetrics();
        if (IsOpen) ResizeWindow();
        UpdatePill();
    }

    /// <summary>Applies the icon/text/spacing settings and keeps the open panel correctly sized.</summary>
    public void ApplyPanelStyle()
    {
        ApplyPanelMetrics();
        Rebuild();
        if (IsOpen)
        {
            ResizeWindow();
            Panel.Set(HeightProperty, TargetPanelHeight());
            Scroller.ScrollToTop();
        }
    }

    /// <summary>Sizes the transparent host window around the panel it has to hold.</summary>
    private void ResizeWindow()
    {
        var hintStrip = _editMode ? 44 : 0; // edit status strip + its top margin
        var width = Math.Round(PanelWidth + CanvasMargin * 2);
        var height = Math.Round(TargetPanelHeight() + PillRowHeight + CanvasMargin * 2 + hintStrip);
        if (Math.Abs(Width - width) > 0.5) Width = width;
        if (Math.Abs(Height - height) > 0.5) Height = height;
    }

    /// <summary>
    /// Grows the host window when the panel needs more room, but never shrinks it while the panel
    /// is open: changing the window size makes the compositor rebuild the layered surface, which
    /// reads as a flash every time a folder is opened.
    /// </summary>
    private void EnsureWindowFits()
    {
        var need = Math.Round(TargetPanelHeight() + PillRowHeight + CanvasMargin * 2 + (_editMode ? 44 : 0));
        if (need <= Height + 0.5) return;
        Height = need;
    }

    private double TargetPanelHeight()
    {
        if (_tiles.Count == 0) return EmptyHeight;

        if (_listMode)
        {
            // A list shows up to twice the configured rows before it scrolls.
            var cap = Math.Max(4, MaxRowCount * 2);
            var fitRows = Math.Max(1, (int)Math.Floor((MaxPanelHeight - PanelChrome) / RowPitch));
            var shown = Math.Min(Math.Min(_tiles.Count, cap), fitRows);
            return PanelChrome + shown * RowPitch;
        }

        var rows = Math.Clamp((int)Math.Ceiling(_tiles.Count / (double)Cols), 1, MaxRowCount);
        var recentExtra = _recentTiles.Count > 0 ? CellHeight + 15 : 0;

        // The screen is the only cap: N configured rows mean an N-row panel, and it scrolls only
        // when those rows genuinely do not fit on this display.
        var available = MaxPanelHeight - PanelChrome - recentExtra;
        var fit = Math.Max(1, (int)Math.Floor(available / CellHeight));
        rows = Math.Min(rows, fit);

        return PanelChrome + rows * CellHeight + recentExtra;
    }

    /// <summary>Sets the panel height in this frame — no animation while typing.</summary>
    private void ApplyPanelHeightNow()
    {
        var target = TargetPanelHeight();
        if (Math.Abs(Panel.ActualHeight - target) < 0.5) return;
        EnsureWindowFits();
        Panel.Set(System.Windows.Controls.Border.HeightProperty, target);
    }

    private void AnimatePanelHeight()
    {
        var target = TargetPanelHeight();
        if (Math.Abs(Panel.ActualHeight - target) < 0.5) return;
        EnsureWindowFits();
        Panel.To(HeightProperty, target, 180, Motion.Out);
    }

    /// <summary>Switches the result surface between the tile grid and the one-line list.</summary>
    private void SetListMode(bool on)
    {
        if (_listMode == on) return;
        _listMode = on;
        ApplyTilesPanel();
    }

    private void ApplyTilesPanel()
    {
        if (_listMode)
        {
            var stack = new FrameworkElementFactory(typeof(StackPanel));
            stack.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
            Tiles.ItemsPanel = new ItemsPanelTemplate(stack);
            Tiles.ItemContainerStyle = (Style)FindResource("RowContainer");
            return;
        }

        var grid = new FrameworkElementFactory(typeof(UniformGrid));
        grid.SetValue(UniformGrid.ColumnsProperty, Cols);
        grid.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
        Tiles.ItemsPanel = new ItemsPanelTemplate(grid);
        Tiles.ItemContainerStyle = (Style)FindResource("TileContainer");
    }

    /// <summary>Rebuilds the tile grid for the configured columns/rows. Called when settings change.</summary>
    public void ApplyLayout()
    {
        ApplyTilesPanel();

        var recentFactory = new FrameworkElementFactory(typeof(UniformGrid));
        recentFactory.SetValue(UniformGrid.ColumnsProperty, Cols);
        recentFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
        RecentTiles.ItemsPanel = new ItemsPanelTemplate(recentFactory);

        ApplyPanelMetrics();
        Rebuild();
        if (IsOpen)
        {
            ResizeWindow();
            Panel.Set(HeightProperty, TargetPanelHeight());
            Scroller.ScrollToTop();
        }
    }

    /// <summary>
    /// Returns a per-item Scale+Translate transform group. The transform must be created
    /// fresh for each tile: a Style-setter RenderTransform is shared and frozen, which throws
    /// "Cannot animate ... sealed or frozen" on every click.
    /// </summary>
    private static TransformGroup EnsureTransforms(FrameworkElement container)
    {
        if (container.RenderTransform is TransformGroup g && g.Children.Count == 2 && !g.IsFrozen)
            return g;

        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform());
        group.Children.Add(new TranslateTransform());
        container.RenderTransform = group;
        container.RenderTransformOrigin = new Point(0.5, 0.5);
        return group;
    }

    /// <summary>While searching, the pointer drives the selection; in edit mode, a drag starts.</summary>
    private void OnTilesMouseMove(object sender, MouseEventArgs e)
    {
        if (_editMode && _dragArmed && e.LeftButton == MouseButtonState.Pressed && _tilePressContainer is { } container)
        {
            var p = e.GetPosition(Tiles);
            if (Math.Abs(p.X - _tilePressPoint.X) > 6 || Math.Abs(p.Y - _tilePressPoint.Y) > 6)
            {
                _dragArmed = false;
                if (_tilePressData is { } data)
                {
                    var obj = new DataObject(InternalTileFormat, data);
                    DragDrop.DoDragDrop(container, obj, DragDropEffects.Move);
                }
            }
            return;
        }

        if (Query.Text.Length == 0) return;

        // Only a genuinely moved pointer may drive the selection. Coordinates are taken
        // relative to the window — NOT to Tiles, which lives inside the scrolling viewer and
        // therefore shifts under a stationary cursor every time the list scrolls or pages.
        // WPF synthesises mouse-move events on such layout changes, and a Tiles-relative
        // anchor would see "movement" that is really just the list scrolling by.
        // After a keyboard step the bar is higher still: ↑/↓ owns the selection until the
        // pointer makes a deliberate move (a few px of tremor must never reclaim it).
        var pos = e.GetPosition(this);
        var threshold = _hoverKeyboardLock ? 18 : 2.5;
        if (_hoverAnchorValid
            && Math.Abs(_hoverAnchor.X - pos.X) <= threshold
            && Math.Abs(_hoverAnchor.Y - pos.Y) <= threshold) return;
        _hoverAnchor = pos;
        _hoverAnchorValid = true;
        _hoverKeyboardLock = false;

        if (ContainerFrom(e.OriginalSource) is { } c && !c.IsSelected) Tiles.SelectedItem = c.DataContext;
    }

    /// <summary>
    /// Anchors hover suppression at the pointer's current spot after a keyboard-driven
    /// selection change: from here, only a real move beyond the locked threshold may hand
    /// the selection back to the mouse.
    /// </summary>
    private void LockHoverToPointer()
    {
        // Window-relative: the same reason as in OnTilesMouseMove — Tiles scrolls.
        _hoverAnchor = Mouse.GetPosition(this);
        _hoverAnchorValid = true;
        _hoverKeyboardLock = true;
    }

    private const string InternalTileFormat = "Jump.InternalTile";

    private static ListBoxItem? ContainerFrom(object source)
    {
        var d = source as DependencyObject;
        while (d is not null and not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }

    private void OnTileDown(object sender, MouseButtonEventArgs e)
    {
        if (ContainerFrom(e.OriginalSource) is not { } c) return;
        var scale = (ScaleTransform)EnsureTransforms(c).Children[0];
        scale.To(ScaleTransform.ScaleXProperty, 0.94, 90);
        scale.To(ScaleTransform.ScaleYProperty, 0.94, 90);
        e.Handled = true;

        _tilePressData = c.DataContext;
        _tilePressContainer = c;
        _tilePressPoint = e.GetPosition(Tiles);

        if (_editMode)
        {
            _dragArmed = true;
        }
        else if (c.DataContext is ShortcutItem or FolderTile)
        {
            _longPressTriggered = false;
            _longPress?.Stop();
            _longPress?.Start();
        }
    }

    private void OnTileUp(object sender, MouseButtonEventArgs e)
    {
        _longPress?.Stop();
        _dragArmed = false;
        if (ContainerFrom(e.OriginalSource) is not { } c) return;
        var scale = (ScaleTransform)EnsureTransforms(c).Children[0];
        scale.To(ScaleTransform.ScaleXProperty, 1, 180, Motion.OutStrong);
        scale.To(ScaleTransform.ScaleYProperty, 1, 180, Motion.OutStrong);
        e.Handled = true;

        if (_longPressTriggered) { _longPressTriggered = false; return; }
        if (_editMode) return; // edit mode: clicks don't launch, only drag/context
        if (!c.IsMouseOver) return;
        Invoke(c.DataContext, c);
    }

    private void Invoke(object? data, FrameworkElement? anchor)
    {
        switch (data)
        {
            case ShortcutItem item: OpenItem(item); break;
            case FolderTile folder: OpenFolder(folder.Name); break;
            case BackTile: OpenFolder(null); break;
            case AddTile: ShowAddMenu(anchor); break;
            case MoreRecentTile: OpenRecentView(); break;
            case FileEntry file: OpenFoundFile(file); break;
            case ResultRow row: RunResultRow(row); break;
        }
    }

    /// <summary>
    /// Opens (or closes) a folder. The contents appear in their own grid below the main one, so
    /// the recently-used row and the top-level tiles stay exactly where they were.
    /// </summary>
    private void OpenFolder(string? name)
    {
        _currentFolder = name;
        _recentView = false;
        Rebuild();

        // Swap in place: the panel takes its new height in this frame and the window is only
        // grown if the folder needs more room than it currently has. No animation, no resize,
        // so nothing outside the grid is repainted.
        Panel.Set(HeightProperty, TargetPanelHeight());
        EnsureWindowFits();
        Scroller.ScrollToTop();
    }

    private void OpenRecentView()
    {
        _recentView = true;
        _currentFolder = null;
        Rebuild();
        Panel.Set(HeightProperty, TargetPanelHeight());
        Scroller.ScrollToTop();
    }

    private void OnRecentTileUp(object sender, MouseButtonEventArgs e)
    {
        if (ContainerFrom(e.OriginalSource) is not { } c) return;
        e.Handled = true;
        if (!c.IsMouseOver) return;
        Invoke(c.DataContext, c);
    }

    private void OpenFoundFile(FileEntry file)
    {
        if (ShellLauncher.OpenPath(file.Path) is { } error) { ShowMessage(error); return; }
        RecordFileUse(file);
        Close(animated: true);
    }

    /// <summary>
    /// Every file opened from the index joins the recently-used row. The entry is recent-only:
    /// it is remembered and re-launchable, but it never becomes a panel tile.
    /// </summary>
    private void RecordFileUse(FileEntry file)
    {
        if (FileIndex.IsAppEntry(file))
            _library.RecordRecent(file.Name, FileIndex.AppAumId(file), ShortcutKind.AppX);
        else
            _library.RecordRecent(file.Name, file.Path,
                file.IsDirectory ? ShortcutKind.Folder : ShortcutKind.File);
    }

    private void OpenSearch(SearchEngineOption engine, string query)
    {
        if (ShellLauncher.OpenUrl(engine.BuildUrl(query)) is { } error)
        {
            ShowMessage(error);
            return;
        }
        // Search-jump clicks are recorded too, named after the engine and the query.
        _library.RecordRecent($"{engine.Name}搜索：{query}", engine.BuildUrl(query), ShortcutKind.Url);
        Close(animated: true);
    }

    // ───────────────────────── "跳转" second level ─────────────────────────

    /// <summary>The query whose jump targets are on screen; null when the normal list is shown.</summary>
    private string? _jumpMenuQuery;

    /// <summary>The single "跳转" row's model, kept while the query stays the same.</summary>
    private JumpEntry? _jumpEntry;

    /// <summary>Opens the target picker of the "跳转" row.</summary>
    private void OpenJumpMenu(string query)
    {
        if (!string.Equals(query, CurrentQuery, StringComparison.Ordinal)) return;
        _jumpMenuQuery = query;
        _lastSlashSignature = null;
        _lastResultSignature = null;
        Rebuild();
    }

    /// <summary>Back to the normal results, with the "跳转" row selected again.</summary>
    private void CloseJumpMenu()
    {
        if (_jumpMenuQuery is null) return;
        _jumpMenuQuery = null;
        _lastSlashSignature = null;
        _lastResultSignature = null;
        Rebuild();
        for (var i = 0; i < _tiles.Count; i++)
        {
            if (_tiles[i] is not ResultRow { Item: JumpEntry }) continue;
            Tiles.SelectedIndex = i;
            if (Tiles.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c) c.BringIntoView();
            LockHoverToPointer();
            break;
        }
    }

    /// <summary>
    /// The second level: one row per enabled jump target, plus a way back. It is a normal result
    /// list, so arrow keys, Enter, hover and clicks all behave the way they do everywhere else.
    /// </summary>
    private void RebuildJumpMenu(string query)
    {
        var engines = SearchEngineCatalog
            .Resolve(CurrentSettings.SearchJumpRules ?? new List<SearchJumpRule>())
            .ToList();

        var rows = new List<ResultRow>
        {
            new()
            {
                Title = "返回",
                Subtitle = $"回到「{query}」的搜索结果（Esc）",
                Glyph = "←",
                Accent = Accent("#64748B"),
                Run = CloseJumpMenu
            }
        };

        foreach (var engine in engines)
        {
            var target = engine;
            rows.Add(new ResultRow
            {
                Title = $"用{target.Name}跳转",
                Subtitle = JumpTargetHint(target),
                Glyph = target.Monogram,
                Accent = target.BrandBrush,
                Run = () => OpenSearch(target, query)
            });
        }

        ShowSlashRows(rows, engines.Count == 0
            ? "还没有启用任何跳转目标，可在设置的「搜索引擎」中添加"
            : string.Empty);

        // The first target, not "返回": the picker was opened to pick something, and Esc / ←
        // already lead back.
        if (engines.Count > 0) Tiles.SelectedIndex = 1;
        ResultCount.Text = engines.Count > 0 ? engines.Count.ToString() : string.Empty;
    }

    /// <summary>The host of a jump rule, which is what tells two targets apart at a glance.</summary>
    private static string JumpTargetHint(SearchEngineOption engine)
    {
        try
        {
            var uri = new Uri(engine.UrlTemplate.Replace("{input}", "x", StringComparison.Ordinal));
            return $"在 {uri.Host} 中打开";
        }
        catch { return "在浏览器中打开"; }
    }

    private void OpenItem(ShortcutItem item)
    {
        var error = ShellLauncher.Open(item);
        if (error is not null)
        {
            if (item.NeedsAvailabilityProbe)
                item.IsUnavailable = !File.Exists(item.Target) && !Directory.Exists(item.Target);
            ShowMessage(error);
            return;
        }
        _library.MarkOpened(item);
        Close(animated: true);
    }

    // ───────────────────────── Search ─────────────────────────

    /// <summary>Applies the configured typing pause (0 = rebuild on every keystroke).</summary>
    public void ApplySearchDebounce()
    {
        var ms = Math.Clamp(App.Current.Settings.SearchDebounceMs, 0, 400);
        _queryDelay.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms));
        _debounceEnabled = ms > 0;
    }

    private void RestartQueryDelay()
    {
        _queryDelay.Stop();

        // The search itself always waits for the configured pause: typing is never allowed to
        // kick off a rebuild, no matter whether the result surface is about to change shape.
        // Only clearing the box (back to the tile grid) applies immediately.
        if (!_debounceEnabled || Query.Text.Length == 0) { OnQueryChanged(); return; }

        _queryDelay.Start();
    }

    /// <summary>Applies a pending keystroke right now (used before Enter / arrow handling).</summary>
    private void FlushQueryDelay()
    {
        if (!_queryDelay.IsEnabled) return;
        _queryDelay.Stop();
        OnQueryChanged();
    }

    private void OnQueryChanged()
    {
        if (!IsOpen) return;
        if (_editMode && Query.Text.Length > 0) ExitEditMode();
        if (Query.Text.Length > 0 && MessageRow.IsHitTestVisible) HideMessage();
        Rebuild();
        ApplyPanelHeightNow();
        if (Scroller.VerticalOffset > 0.5) Scroller.ScrollToTop();
        UpdatePill();
    }

    private bool PillWanted =>
        CurrentSettings.SearchBoxVisible || Query.Text.Length > 0 || _messageTimer.IsEnabled || MessageRow.IsHitTestVisible;

    private void UpdatePill()
    {
        var show = PillWanted;
        Pill.IsHitTestVisible = show;

        // Coming from "no pill at all" (the first keystroke after opening) must be instant: the
        // typed character is already inside this box, so any fade reads as input lag.
        if (show && Pill.Opacity < 0.05)
        {
            Pill.Set(OpacityProperty, 1);
            PillShift.Set(TranslateTransform.YProperty, 0);
        }
        else
        {
            Pill.To(OpacityProperty, show ? 1 : 0, show ? 160 : 140, show ? Motion.Out : Motion.In);
            PillShift.To(TranslateTransform.YProperty, show ? 0 : 6, show ? 220 : 140, show ? Motion.OutStrong : Motion.In);
        }

        // A message always swaps into the pill in place of the search box — never overlaps it.
        // With the search box hidden by default this is the old pill behavior; with it always
        // visible the toast briefly replaces the query text (which stays intact underneath).
        bool message = MessageRow.IsHitTestVisible;
        SearchRow.To(OpacityProperty, message ? 0 : 1, 120);
        MessageRow.To(OpacityProperty, message ? 1 : 0, 120);
    }

    // ───────────────────────── Messages ─────────────────────────

    private void ShowMessage(string text, string? actionText = null, Action? action = null)
    {
        MessageText.Text = text;
        _messageAction = action;
        MessageAction.Content = actionText;
        MessageAction.Visibility = actionText is null ? Visibility.Collapsed : Visibility.Visible;
        MessageRow.IsHitTestVisible = true;
        _messageTimer.Stop();
        _messageTimer.Start();
        UpdatePill();
    }

    private void HideMessage(bool immediate = false)
    {
        _messageTimer.Stop();
        MessageRow.IsHitTestVisible = false;
        _messageAction = null;
        if (immediate)
        {
            MessageRow.Set(OpacityProperty, 0);
            SearchRow.Set(OpacityProperty, 1);
            Pill.Set(OpacityProperty, CurrentSettings.SearchBoxVisible ? 1 : 0);
            Pill.IsHitTestVisible = CurrentSettings.SearchBoxVisible;
            PillShift.Set(TranslateTransform.YProperty, CurrentSettings.SearchBoxVisible ? 0 : 6);
            return;
        }
        UpdatePill();
    }

    // ───────────────────────── Keyboard ─────────────────────────

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_renaming is not null || _renamingFolder is not null) return; // rename box handles its own keys
        if (_modalDepth > 0) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // Ctrl+Enter rebuilds the local index from anywhere in the panel — tile grid, plain
        // results, /s results or an empty search box — as long as file search is on.
        if (ctrl && e.Key == Key.Enter && CurrentSettings.FileSearchEnabled)
        {
            RebuildFileIndexNow();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (_editMode) ExitEditMode();
                else if (_jumpMenuQuery is not null) CloseJumpMenu();
                else if (Query.Text.Length > 0) Query.Text = string.Empty;
                else if (_recentView) OpenFolder(null);
                else if (_currentFolder is not null) OpenFolder(null);
                else Close(animated: true);
                e.Handled = true;
                break;

            case Key.Back when Query.Text.Length == 0 && (_currentFolder is not null || _recentView):
                // 进/出分组只由 Enter 与 Esc 触发；Back 交给输入框自行处理。
                e.Handled = true;
                break;

            case Key.Enter:
                FlushQueryDelay();
                if (SelectedGridItem() is { } selected) Invoke(selected, null);
                else if (Tiles.SelectedItem is { } row) Invoke(row, null);
                e.Handled = true;
                break;

            // Tile grid: ←/→ walk the grid (and the recent row) horizontally; in the search
            // result list ←/→ do nothing — they only move the search box caret.
            case Key.Left when !_listMode: FlushQueryDelay(); MoveGridSelection(-1, wrap: true); e.Handled = true; break;
            case Key.Right when !_listMode: FlushQueryDelay(); MoveGridSelection(1, wrap: true); e.Handled = true; break;

            // In the one-line list there are no columns: up/down must walk the list one row
            // at a time (like a search dropdown), otherwise every press skips Cols items.
            case Key.Up:
                FlushQueryDelay();
                if (_listMode) MoveSelection(-1, wrap: false); else MoveGridSelection(-Cols, wrap: false);
                e.Handled = true;
                break;
            case Key.Down:
                FlushQueryDelay();
                if (_listMode) MoveSelection(1, wrap: false); else MoveGridSelection(Cols, wrap: false);
                e.Handled = true;
                break;
            case Key.Tab:
                if (_listMode) MoveSelection((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1, wrap: true);
                else MoveGridSelection((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1, wrap: true);
                e.Handled = true;
                break;

            case Key.F2:
                if (SelectedGridItem() is ShortcutItem r) BeginRename(r);
                e.Handled = true;
                break;

            case Key.Delete when Query.Text.Length == 0:
                if (SelectedGridItem() is ShortcutItem d) { Remove(d); e.Handled = true; }
                break;

            case Key.OemComma when ctrl:
                App.Current.OpenSettings();
                e.Handled = true;
                break;

            case Key.V when ctrl:
            {
                // Empty search box → Ctrl+V adds from clipboard. Otherwise, only an
                // Explorer file copy (unambiguous) is intercepted; plain text pastes normally.
                bool tryAdd = Query.Text.Length == 0;
                if (!tryAdd)
                {
                    try { tryAdd = Clipboard.ContainsFileDropList(); } catch { tryAdd = false; }
                }
                if (tryAdd)
                {
                    if (TryPasteAdd(out var msg)) e.Handled = true;
                    else if (Query.Text.Length == 0 && msg is not null) { ShowMessage(msg); e.Handled = true; }
                }
                break;
            }

            case Key.Z when ctrl && Query.Text.Length == 0:
                Undo();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta, bool wrap)
    {
        // A divider is not selectable, so navigation walks the selectable rows only.
        var selectable = new List<int>(_tiles.Count);
        for (var i = 0; i < _tiles.Count; i++)
            if (_tiles[i] is not DividerRow) selectable.Add(i);
        if (selectable.Count == 0 || delta == 0) return;

        var current = selectable.IndexOf(Tiles.SelectedIndex);
        int position;
        if (current < 0) position = delta > 0 ? 0 : selectable.Count - 1;
        else
        {
            position = current + delta;
            position = wrap
                ? ((position % selectable.Count) + selectable.Count) % selectable.Count
                : Math.Clamp(position, 0, selectable.Count - 1);
        }

        if (position == current) return; // already at the end: stay there instead of jumping around
        var next = selectable[position];
        Tiles.SelectedIndex = next;
        if (Tiles.ItemContainerGenerator.ContainerFromIndex(next) is FrameworkElement c) c.BringIntoView();
        LockHoverToPointer(); // the keyboard owns the selection now; hover must earn it back
    }

    /// <summary>The item the keyboard currently addresses, across the recent row and the grid.</summary>
    private object? SelectedGridItem() =>
        RecentTiles.SelectedIndex >= 0 ? RecentTiles.SelectedItem
        : Tiles.SelectedIndex >= 0 ? Tiles.SelectedItem : null;

    /// <summary>
    /// Tile-grid navigation walks one combined sequence: the recently-used row first, then the
    /// grid. ↑/↓ move a full row, ←/→ move one step with wrap-around, so the keyboard reaches
    /// the recent tiles without ever leaving it.
    /// </summary>
    private void MoveGridSelection(int delta, bool wrap)
    {
        var recentCount = RecentSection.Visibility == Visibility.Visible ? RecentTiles.Items.Count : 0;
        var count = recentCount + Tiles.Items.Count;
        if (count == 0 || delta == 0) return;

        int current;
        if (RecentTiles.SelectedIndex >= 0 && recentCount > 0) current = RecentTiles.SelectedIndex;
        else if (Tiles.SelectedIndex >= 0) current = recentCount + Tiles.SelectedIndex;
        else current = -1; // nothing selected yet: the first step picks the first item

        var target = current < 0 ? (delta > 0 ? 0 : count - 1) : current + delta;
        if (wrap) target = ((target % count) + count) % count;
        else target = Math.Clamp(target, 0, count - 1);
        if (target == current) return;

        if (target < recentCount)
        {
            RecentTiles.SelectedIndex = target;
            Tiles.SelectedIndex = -1;
            if (RecentTiles.ItemContainerGenerator.ContainerFromIndex(target) is FrameworkElement r)
                r.BringIntoView();
        }
        else
        {
            Tiles.SelectedIndex = target - recentCount;
            RecentTiles.SelectedIndex = -1;
            if (Tiles.ItemContainerGenerator.ContainerFromIndex(target - recentCount) is FrameworkElement t)
                t.BringIntoView();
        }
    }

    // ───────────────────────── Context menu & editing ─────────────────────────

    private void OnTileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ContainerFrom(e.OriginalSource) is not { } c) return;
        e.Handled = true;

        switch (c.DataContext)
        {
            case ResultRow { Item: FileEntry file }: ShowFileMenu(file); break;
            case ShortcutItem item: ShowShortcutMenu(item, c); break;
            case FolderTile folder: ShowFolderMenu(folder, c); break;
            case AddTile: ShowAddMenu(c); break;
            case BackTile: OpenFolder(null); break;
        }
    }

    /// <summary>
    /// Menus created in code have no logical parent, so they miss the window's pixel-grid
    /// settings (UseLayoutRounding / TextFormattingMode=Display) and their text renders
    /// blurry on fractional display scaling (125% / 150%). Every menu goes through here.
    /// </summary>
    private static ContextMenu NewMenu(FrameworkElement? anchor = null, bool centered = false)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = centered
                ? System.Windows.Controls.Primitives.PlacementMode.Center
                : System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };
        TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
        return menu;
    }

    private void ShowFileMenu(FileEntry file)
    {
        var menu = NewMenu();
        menu.Items.Add(Item("打开", () => OpenFoundFile(file), "Enter"));

        // Packaged apps have no filesystem location: revealing or copying their shell: path
        // would only produce an error, so their menu stops at "打开".
        if (FileIndex.IsAppEntry(file))
        {
            menu.Closed += (_, _) => { if (IsOpen && _renaming is null) Keyboard.Focus(Query); };
            menu.IsOpen = true;
            return;
        }

        menu.Items.Add(Item("打开所在位置", () => ShellLauncher.RevealPath(file.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("添加到 Jump", () =>
        {
            if (ShellLauncher.FromInput(file.Path) is not { } item) return;
            Commit(new List<ShortcutItem> { item });
            ShowMessage($"已添加「{item.Name}」");
        }));
        menu.Items.Add(Item("复制路径", () =>
        {
            try { Clipboard.SetText(file.Path); ShowMessage("路径已复制"); }
            catch { ShowMessage("复制失败：剪贴板被占用"); }
        }));
        menu.Closed += (_, _) => { if (IsOpen && _renaming is null) Keyboard.Focus(Query); };
        menu.IsOpen = true;
    }

    private void ShowFolderMenu(FolderTile folder, FrameworkElement anchor)
    {
        var menu = NewMenu(anchor);
        menu.Items.Add(Item("打开分组", () => OpenFolder(folder.Name)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("重命名分组…", () => BeginRenameFolder(folder.Name)));
        menu.Items.Add(Item("解散分组", () => DissolveFolder(folder.Name)));
        menu.Closed += (_, _) => { if (IsOpen && _renaming is null) Keyboard.Focus(Query); };
        menu.IsOpen = true;
    }

    private string? _renamingFolder;

    private void BeginRenameFolder(string name)
    {
        _renamingFolder = name;
        _renaming = null;
        RenameBox.Text = name;
        RenameOverlay.Visibility = Visibility.Visible;
        RenameOverlay.To(OpacityProperty, 1, 150);
        Dispatcher.BeginInvoke(() =>
        {
            RenameBox.Focus();
            Keyboard.Focus(RenameBox);
            RenameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void ShowShortcutMenu(ShortcutItem item, FrameworkElement anchor)
    {
        Tiles.SelectedItem = item;

        var menu = NewMenu(anchor);
        menu.Items.Add(Item("打开", () => OpenItem(item), "Enter"));
        if (item.Kind is ShortcutKind.File or ShortcutKind.Folder or ShortcutKind.App)
            menu.Items.Add(Item("打开所在位置", () => { ShellLauncher.Reveal(item); Close(animated: true); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(item.Pinned ? "取消固定" : "固定在最前", () => TogglePin(item)));
        menu.Items.Add(Item("重命名", () => BeginRename(item), "F2"));

        // Move between folders without dragging: each target is one direct menu item. A nested
        // submenu here was flaky (clicking the header did nothing), and with realistic folder
        // counts a flat list reads faster anyway.
        var folders = _library.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Folder))
            .Select(i => i.Folder!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (folders.Count > 0 || item.Folder is not null)
        {
            if (item.Folder is not null)
                menu.Items.Add(Item("移动到主面板", () => MoveToFolder(item, null)));
            foreach (var folder in folders)
            {
                if (SameFolder(folder, item.Folder)) continue;
                var target = folder;
                menu.Items.Add(Item($"移动到「{target}」", () => MoveToFolder(item, target)));
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("从 Jump 中移除", () => Remove(item), "Del"));
        menu.Closed += (_, _) => { if (IsOpen && _renaming is null) Keyboard.Focus(Query); };
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty };
        mi.Click += (_, _) => action();
        return mi;
    }

    private void TogglePin(ShortcutItem item)
    {
        item.Pinned = !item.Pinned;
        _library.SaveSoon();
        _ordered = _library.Ordered();
        Rebuild();
        ShowMessage(item.Pinned ? $"已固定「{item.Name}」" : $"已取消固定「{item.Name}」");
    }

    private void Remove(ShortcutItem item)
    {
        var index = _library.Remove(item);
        if (index < 0) return;
        _lastRemoved = (item, index);
        ShowMessage($"已移除「{item.Name}」", "撤销", Undo);
    }

    private void Undo()
    {
        if (_lastRemoved is not { } last) return;
        _lastRemoved = null;
        _library.Restore(last.item, last.index);
        ShowMessage($"已恢复「{last.item.Name}」");
    }

    private void BeginRename(ShortcutItem item)
    {
        _renaming = item;
        RenameBox.Text = item.Name;
        RenameOverlay.Visibility = Visibility.Visible;
        RenameOverlay.To(OpacityProperty, 1, 150);
        Dispatcher.BeginInvoke(() =>
        {
            RenameBox.Focus();
            Keyboard.Focus(RenameBox);
            RenameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var name = RenameBox.Text.Trim();
            if (_renamingFolder is not null && name.Length > 0)
            {
                var old = _renamingFolder;
                _renamingFolder = null;
                RenameFolder(old, name);
            }
            else if (_renaming is not null && name.Length > 0)
            {
                _renaming.Name = name;
                _library.SaveSoon();
            }
            HideRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideRename();
            e.Handled = true;
        }
    }

    private void HideRename(bool immediate = false)
    {
        _renaming = null;
        _renamingFolder = null;
        if (immediate)
        {
            RenameOverlay.Set(OpacityProperty, 0);
            RenameOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        RenameOverlay.To(OpacityProperty, 0, 130, Motion.In, completed: () =>
        {
            if (_renaming is null && _renamingFolder is null) RenameOverlay.Visibility = Visibility.Collapsed;
        });
        Keyboard.Focus(Query);
    }

    // ───────────────────────── Settings ─────────────────────────

    public void ShowSettings() => App.Current.OpenSettings();

    /// <summary>Shows a notice immediately when open, or on the next open otherwise.</summary>
    public void ShowNotice(string text)
    {
        if (IsOpen) ShowMessage(text);
        else _pendingNotice = text;
    }

    // ───────────────────────── Adding ─────────────────────────

    private void ShowAddMenu(FrameworkElement? anchor)
    {
        var menu = NewMenu(anchor ?? (FrameworkElement)Panel, centered: anchor is null);
        menu.Items.Add(Item("添加文件 / 应用…", () => AddViaDialog(folder: false)));
        menu.Items.Add(Item("添加文件夹…", () => AddViaDialog(folder: true)));
        menu.Items.Add(Item("添加商店应用…", ShowStoreAppPicker));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("粘贴路径或网址", () => { if (!TryPasteAdd(out _)) ShowMessage("剪贴板里没有可添加的路径或网址"); }, "Ctrl+V"));
        menu.Closed += (_, _) => { if (IsOpen) Keyboard.Focus(Query); };
        menu.IsOpen = true;
    }

    // ───────────────────────── Store apps ─────────────────────────

    private async void ShowStoreAppPicker()
    {
        _modalDepth++;
        try
        {
            ShowMessage("正在读取已安装的商店应用…");
            var apps = await Task.Run(StoreAppsService.Enumerate);
            if (!IsOpen) return;
            HideMessage(immediate: true);

            if (apps.Count == 0)
            {
                ShowMessage("没有找到已安装的商店应用");
                return;
            }
            var picker = new StoreAppPicker(apps) { Owner = this };
            if (picker.ShowDialog() == true && picker.Selected is { } chosen)
            {
                var item = new ShortcutItem
                {
                    Kind = ShortcutKind.AppX,
                    Target = chosen.AppUserModelId,
                    Name = chosen.Name,
                    Folder = _currentFolder
                };
                var added = _library.Add(new[] { item });
                if (added.Count > 0)
                {
                    _ = Task.Run(() =>
                    {
                        var logo = StoreAppsService.GetLogo(chosen.AppUserModelId, 256);
                        if (logo is not null) Dispatcher.BeginInvoke(() => item.Icon = logo);
                    });
                    ShowMessage($"已添加「{chosen.Name}」");
                }
                else ShowMessage("这个应用已经在 Jump 里了");
            }
        }
        catch (Exception ex) { ShowMessage(ex.Message); }
        finally
        {
            _modalDepth--;
            if (IsOpen) { Activate(); Keyboard.Focus(Query); }
        }
    }

    private void AddViaDialog(bool folder)
    {
        _modalDepth++;
        try
        {
            if (folder)
            {
                var dlg = new OpenFolderDialog { Title = "选择要添加的文件夹", Multiselect = true };
                if (dlg.ShowDialog(this) == true) AddPaths(dlg.FolderNames);
            }
            else
            {
                var dlg = new OpenFileDialog
                {
                    Title = "选择要添加的文件或应用",
                    Multiselect = true,
                    DereferenceLinks = false,
                    Filter = "所有文件|*.*|应用程序|*.exe;*.lnk;*.bat;*.cmd"
                };
                if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
            }
        }
        finally
        {
            _modalDepth--;
            if (IsOpen)
            {
                Activate();
                Keyboard.Focus(Query);
            }
        }
    }

    private bool TryPasteAdd(out string? message)
    {
        message = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                return AddPaths(files) >= 0;
            }
            if (Clipboard.ContainsText())
            {
                var lines = Clipboard.GetText().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var items = lines.Select(ShellLauncher.FromInput).OfType<ShortcutItem>().ToList();
                if (items.Count == 0)
                {
                    message = "剪贴板里没有可添加的路径或网址";
                    return false;
                }
                Commit(items);
                return true;
            }
            message = "剪贴板里没有可添加的路径或网址";
        }
        catch
        {
            message = "无法读取剪贴板";
        }
        return false;
    }

    private int AddPaths(IEnumerable<string> paths)
    {
        var items = paths.Select(ShellLauncher.FromInput).OfType<ShortcutItem>().ToList();
        if (items.Count == 0) { ShowMessage("没有可添加的项目"); return -1; }
        return Commit(items);
    }

    private int Commit(List<ShortcutItem> items)
    {
        // Adding while inside a group puts the new entries into that group, not on the top level.
        if (_currentFolder is not null)
            foreach (var item in items)
                if (item.Folder is null) item.Folder = _currentFolder;

        var added = _library.Add(items);
        _ = IconService.LoadAsync(added);
        if (added.Count == 0) ShowMessage("这些已经在 Jump 里了");
        else if (added.Count == 1) ShowMessage($"已添加「{added[0].Name}」");
        else ShowMessage($"已添加 {added.Count} 项");
        return added.Count;
    }

    private void OnLibraryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsOpen || _closing) return;
        // New items join at the end of the current order instead of reshuffling what you see.
        var current = new HashSet<ShortcutItem>(_library.Items);
        var kept = _ordered.Where(current.Contains).ToList();
        // Recent-only bookkeeping entries live in the recent row, never in the tile grid.
        foreach (var i in _library.Items) if (!i.RecentOnly && !kept.Contains(i)) kept.Add(i);
        // Keep pinned first.
        _ordered = kept.Where(i => i.Pinned).Concat(kept.Where(i => !i.Pinned)).ToList();
        Rebuild();
        AnimatePanelHeight();
    }

    // ───────────────────────── Drag & drop ─────────────────────────

    private static bool HasDroppable(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text);

    private static bool HasInternalTile(DragEventArgs e) => e.Data.GetDataPresent(InternalTileFormat);

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (HasInternalTile(e))
        {
            e.Effects = DragDropEffects.Move;
            BeginInternalDrag(e);
            return;
        }
        if (!HasDroppable(e)) { e.Effects = DragDropEffects.None; return; }
        _externalFileDrag = true; // keep the panel open for the whole external drag
        e.Effects = DragDropEffects.Link | DragDropEffects.Copy;
        SetDropOverlay(true);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (HasInternalTile(e))
        {
            e.Effects = DragDropEffects.Move;
            TrackInternalDrag(e);
            e.Handled = true;
            return;
        }
        if (!HasDroppable(e)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Link | DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only hide when truly outside.
        var p = e.GetPosition(Panel);
        if (p.X > 0 && p.Y > 0 && p.X < Panel.ActualWidth && p.Y < Panel.ActualHeight) return;
        _externalFileDrag = false;
        SetDropOverlay(false);
        EndInternalDrag();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(InternalTileFormat) is { } raw)
        {
            HandleInternalDrop(raw, e);
            EndInternalDrag();
            return;
        }

        _externalFileDrag = false;
        SetDropOverlay(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            AddPaths(files);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text)
        {
            var items = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ShellLauncher.FromInput).OfType<ShortcutItem>().ToList();
            if (items.Count > 0) Commit(items);
        }
        Activate();
        Keyboard.Focus(Query);
    }

    /// <summary>Entry point for drops on the floating ball: make sure the panel is visible,
    /// then add the files (the same path an in-panel drop takes).</summary>
    public void ReceiveExternalFiles(IList<string> paths)
    {
        if (!IsOpen || _closing) Open();
        AddPaths(paths);
    }

    private void SetDropOverlay(bool show)
    {
        DropOverlay.To(OpacityProperty, show ? 1 : 0, show ? 140 : 160, show ? Motion.Out : Motion.In);
    }

    // ───────────────────────── Edit mode & folders ─────────────────────────

    private void EnterEditMode()
    {
        if (_editMode) return;
        _editMode = true;
        EditGlow.To(OpacityProperty, 1, 160, Motion.Out);

        // A persistent strip under the panel names the mode and the gestures. The window grows
        // downwards to make room, so the panel itself does not move.
        EditHintBar.Visibility = Visibility.Visible;
        EditHintBar.To(OpacityProperty, 1, 160, Motion.Out);
        ResizeWindow();

        ShowStickyMessage("调整布局中 · Esc 退出", "完成", ExitEditMode);
    }

    private void ExitEditMode()
    {
        if (!_editMode) return;
        _editMode = false;
        _dragArmed = false;
        EndInternalDrag();
        EditGlow.To(OpacityProperty, 0, 140, Motion.In);
        EditHintBar.To(OpacityProperty, 0, 120, Motion.In);
        EditHintBar.Visibility = Visibility.Collapsed;
        HideMessage(immediate: true);
        Rebuild();
        ResizeWindow();
    }

    // ───────────────────────── Internal drag: gap preview + auto-scroll ─────────────────────────

    /// <summary>
    /// Cell under <paramref name="p"/> (ListBox coordinates), derived from the grid geometry
    /// instead of from the container under the cursor: while the placeholder inserts itself the
    /// containers shift, and hit-testing them would make the preview oscillate.
    /// </summary>
    private (int index, double relX) CellAt(Point p)
    {
        var cellW = Math.Max(1, CellWidth);
        var cellH = Math.Max(1, CellHeight);
        var col = Math.Clamp((int)Math.Floor(p.X / cellW), 0, Cols - 1);
        var row = Math.Max(0, (int)Math.Floor(p.Y / cellH));
        var relX = Math.Clamp((p.X - col * cellW) / cellW, 0, 1);
        var index = Math.Clamp(row * Cols + col, 0, Math.Max(0, _tiles.Count - 1));
        return (index, relX);
    }

    private void BeginInternalDrag(DragEventArgs e)
    {
        if (!_editMode) return;
        _draggingTile = true;
        _lastDragPoint = e.GetPosition(Tiles);
        UpdateInsertMarkerAt(_lastDragPoint);
        UpdateDragAutoScroll(e);
        if (!_dragScroll.IsEnabled) _dragScroll.Start();
    }

    private void TrackInternalDrag(DragEventArgs e)
    {
        if (!_editMode) { e.Effects = DragDropEffects.None; return; }
        _draggingTile = true;
        _lastDragPoint = e.GetPosition(Tiles);
        UpdateInsertMarkerAt(_lastDragPoint);
        UpdateDragAutoScroll(e);
        if (!_dragScroll.IsEnabled) _dragScroll.Start();
    }

    private void EndInternalDrag()
    {
        _draggingTile = false;
        _dragScrollStep = 0;
        _dragScroll?.Stop();
        RemoveInsertMarker();
    }

    /// <summary>Scrolls faster the closer the pointer is to the top/bottom edge of the list.</summary>
    private void UpdateDragAutoScroll(DragEventArgs e)
    {
        const double edge = 46;
        var p = e.GetPosition(Scroller);
        var height = Math.Max(1, Scroller.ActualHeight);
        if (p.Y < edge) _dragScrollStep = -(1 - p.Y / edge) * 18;
        else if (p.Y > height - edge) _dragScrollStep = (1 - (height - p.Y) / edge) * 18;
        else _dragScrollStep = 0;
    }

    private void UpdateInsertMarkerAt(Point p)
    {
        if (!_draggingTile || !_editMode) return;
        if (p.Y < 0 || p.Y > Scroller.ActualHeight + 200) return;

        var (index, relX) = CellAt(p);
        var want = Math.Clamp(index + (relX >= 0.5 ? 1 : 0), 0, _tiles.Count);
        if (_insertMarker is not null && want == _markerIndex) return;

        RemoveInsertMarker();
        _insertMarker = InsertMarker.Instance;
        _tiles.Insert(Math.Min(want, _tiles.Count), _insertMarker);
        _markerIndex = want;
        SyncHeightWithMarker();
    }

    private void RemoveInsertMarker()
    {
        if (_insertMarker is null) return;
        var marker = _insertMarker;
        _insertMarker = null;
        _markerIndex = -1;
        _tiles.Remove(marker);
        SyncHeightWithMarker();
    }

    /// <summary>The placeholder may push the grid onto a new row; let the panel grow with it.</summary>
    private void SyncHeightWithMarker()
    {
        var target = TargetPanelHeight();
        if (Math.Abs(Panel.ActualHeight - target) < 0.5) return;
        ResizeWindow();
        Panel.To(HeightProperty, target, 160, Motion.Out);
    }

    private void HandleInternalDrop(object raw, DragEventArgs e)
    {
        var (index, relX) = CellAt(e.GetPosition(Tiles));

        // The placeholder is a preview only; take it out before touching the real order.
        RemoveInsertMarker();
        _draggingTile = false;
        _dragScrollStep = 0;
        _dragScroll.Stop();

        var target = _tiles.ElementAtOrDefault(index);

        // A folder tile is not one library item; move its first member so the whole
        // group tile lands before/after the target.
        if (raw is FolderTile draggedFolder)
        {
            var draggedAnchor = draggedFolder.Members.FirstOrDefault();
            var targetAnchor = target switch
            {
                ShortcutItem s => s,
                FolderTile f => f.Members.FirstOrDefault(),
                _ => null
            };
            if (draggedAnchor is null || targetAnchor is null) return;
            if (ReferenceEquals(draggedAnchor, targetAnchor)) return;

            _library.Reorder(draggedAnchor, targetAnchor, after: relX >= 0.5);
            RefreshOrder();
            return;
        }

        if (raw is not ShortcutItem dragged) return;

        if (target is ShortcutItem targetItem && !ReferenceEquals(targetItem, dragged))
        {
            // Left/right edge → reorder; centre → merge into a folder.
            if (relX < 0.32)
            {
                _library.Reorder(dragged, targetItem, after: false);
                RefreshOrder();
            }
            else if (relX > 0.68)
            {
                _library.Reorder(dragged, targetItem, after: true);
                RefreshOrder();
            }
            else
            {
                MergeIntoFolder(dragged, targetItem);
            }
        }
        else if (target is FolderTile folder)
        {
            var anchor = folder.Members.FirstOrDefault();
            if (anchor is null) return;
            if (relX < 0.32)
            {
                _library.Reorder(dragged, anchor, after: false);
                RefreshOrder();
            }
            else if (relX > 0.68)
            {
                _library.Reorder(dragged, anchor, after: true);
                RefreshOrder();
            }
            else
            {
                MoveToFolder(dragged, folder.Name);
            }
        }
    }


    private ListBoxItem? FindContainerAt(Point p)
    {
        var hit = Tiles.InputHitTest(p) as DependencyObject;
        while (hit is not null and not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        return hit as ListBoxItem;
    }

    private void RefreshOrder()
    {
        _ordered = _library.Ordered();
        Rebuild();
        AnimatePanelHeight();
        ShowStickyMessage("调整布局中 · 已调整 · Esc 退出", "完成", ExitEditMode);
    }

    private void MergeIntoFolder(ShortcutItem a, ShortcutItem b)
    {
        var name = string.IsNullOrWhiteSpace(b.Folder) ? NextFolderName() : b.Folder!;
        a.Folder = name;
        b.Folder = name;
        _library.SaveSoon();
        _ordered = _library.Ordered();
        Rebuild();
        AnimatePanelHeight();
        ShowStickyMessage("调整布局中 · 已分组 · Esc 退出", "完成", ExitEditMode);
    }

    private void MoveToFolder(ShortcutItem item, string? folderName)
    {
        var wasFolder = item.Folder;
        item.Folder = folderName;
        _library.SaveSoon();
        _ordered = _library.Ordered();
        Rebuild();
        ResizeWindow();
        AnimatePanelHeight();
        if (_editMode)
            ShowStickyMessage("调整布局中 · 已移动 · Esc 退出", "完成", ExitEditMode);
        else
            ShowMessage(folderName is null
                ? $"「{item.Name}」已移回主面板"
                : $"「{item.Name}」已移动到「{folderName}」");
        if (wasFolder is not null && !_ordered.Any(i => SameFolder(i.Folder, wasFolder)))
            _currentFolder = SameFolder(_currentFolder, wasFolder) ? null : _currentFolder;
    }

    private string NextFolderName()
    {
        var existing = new HashSet<string>(
            _library.Items.Where(i => !string.IsNullOrWhiteSpace(i.Folder)).Select(i => i.Folder!),
            StringComparer.CurrentCultureIgnoreCase);
        var n = 1;
        while (existing.Contains($"分组 {n}")) n++;
        return $"分组 {n}";
    }

    private void RenameFolder(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return;
        foreach (var i in _library.Items.Where(x => SameFolder(x.Folder, oldName))) i.Folder = newName;
        _library.SaveSoon();
        _ordered = _library.Ordered();
        if (_currentFolder is not null && SameFolder(_currentFolder, oldName)) _currentFolder = newName;
        Rebuild();
        ShowMessage($"已重命名为「{newName}」");
    }

    private void DissolveFolder(string name)
    {
        foreach (var i in _library.Items.Where(x => SameFolder(x.Folder, name))) i.Folder = null;
        _library.SaveSoon();
        _ordered = _library.Ordered();
        if (_currentFolder is not null && SameFolder(_currentFolder, name)) _currentFolder = null;
        Rebuild();
        AnimatePanelHeight();
        ShowMessage($"已解散「{name}」");
    }

    private void ShowStickyMessage(string text, string? actionText, Action? action)
    {
        MessageText.Text = text;
        _messageAction = action;
        MessageAction.Content = actionText;
        MessageAction.Visibility = actionText is null ? Visibility.Collapsed : Visibility.Visible;
        MessageRow.IsHitTestVisible = true;
        _messageTimer.Stop(); // sticky: no auto-hide
        UpdatePill();
    }
}
