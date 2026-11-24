using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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

/// <summary>
/// The centred panel. Nothing but your shortcuts: a grid of tiles, type-to-filter, click to open.
/// The search pill only exists while you are typing (or for a brief message).
/// </summary>
public partial class LauncherWindow : Window
{
    private const double PanelChrome = 2 + 28; // border + inner padding
    private const double EmptyHeight = 210;
    private const double SettingsHeight = 470;
    private const double CanvasMargin = 36;
    private const double PillRowHeight = 50; // 40 px pill + 10 px gap
    private const double IconTopPadding = 8;
    private const double TileBottomPadding = 8;

    private static AppSettings CurrentSettings => App.Current.Settings;

    private double TileIconSize => Math.Clamp(CurrentSettings.PanelIconSize, 32, 48);
    private double TileFontSize => Math.Clamp(CurrentSettings.PanelFontSize, 10, 14);
    private double TileGap => Math.Clamp(CurrentSettings.PanelGap, 2, 10);
    private double LabelLineHeight => Math.Round(TileFontSize * 1.33);
    private double LabelArea => LabelLineHeight * 2;
    private double TileHeight => IconTopPadding + TileIconSize + TileGap + LabelArea + TileBottomPadding;

    private int Cols => Math.Clamp(App.Current.Settings.Columns, 3, 5);
    private int MaxRowCount => Math.Clamp(App.Current.Settings.MaxRows, 2, 5);

    private readonly ShortcutLibrary _library;
    private readonly ObservableCollection<object> _tiles = new();
    private readonly DispatcherTimer _messageTimer;

    private List<ShortcutItem> _ordered = new();
    private string? _currentFolder;
    private bool _closing;
    private bool _staggerNext;
    private int _modalDepth;
    private ShortcutItem? _renaming;
    private (ShortcutItem item, int index)? _lastRemoved;
    private Action? _messageAction;
    private string? _pendingNotice;

    // Settings overlay state.
    private bool _settingsOpen;
    private bool _capturingHotkey;
    private bool _settingsRefreshing;
    private const string HotkeyHintDefault = "点击后按下新的组合键，Esc 取消";

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

        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _messageTimer.Tick += (_, _) => HideMessage();

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

        Deactivated += (_, _) => { if (IsOpen && _modalDepth == 0) Close(animated: true); };
        PreviewKeyDown += OnKey;
        PreviewMouseDown += (_, e) =>
        {
            if (!_capturingHotkey) return;
            if (e.OriginalSource is DependencyObject source && IsInside(source, HotkeyBox)) return;
            CancelHotkeyCapture();
        };
        Query.TextChanged += (_, _) => OnQueryChanged();

        Tiles.PreviewMouseMove += OnTilesMouseMove;
        Tiles.AddHandler(ListBoxItem.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnTileDown), true);
        Tiles.AddHandler(ListBoxItem.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnTileUp), true);
        Tiles.AddHandler(ListBoxItem.MouseRightButtonUpEvent, new MouseButtonEventHandler(OnTileRightClick), true);

        EmptyState.MouseLeftButtonUp += (_, _) => ShowAddMenu(null);
        MessageAction.Click += (_, _) => { var a = _messageAction; HideMessage(); a?.Invoke(); };
        RenameBox.PreviewKeyDown += OnRenameKey;

        WheelScroller.Attach(this, () => _settingsOpen ? null : Scroller);

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

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
            _settingsOpen = false;
            _capturingHotkey = false;
            Query.IsEnabled = true;
            SettingsOverlay.Visibility = Visibility.Collapsed;
            SettingsOverlay.Set(OpacityProperty, 0);

            Query.Text = string.Empty;
            HideMessage(immediate: true);
            HideRename(immediate: true);
            _currentFolder = null;
            _ordered = _library.Ordered();
            _staggerNext = true;
            Rebuild();
            var panelHeight = TargetPanelHeight();
            Panel.Set(HeightProperty, panelHeight);
            Scroller.ScrollToTop();

            // Top-anchored stack: the search pill and panel top stay put while the result
            // count (and therefore the panel height) changes. The window itself is placed
            // so the initial panel is optically centred on the monitor under the pointer.
            var wa = ScreenGeometry.WorkAreaAtCursor(this);
            Left = wa.Left + (wa.Width - Width) / 2;
            var desiredTop = wa.Top + wa.Height / 2 - CanvasMargin - PillRowHeight - panelHeight / 2 - 16;
            Top = Math.Clamp(desiredTop, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));

            Stack.Set(OpacityProperty, 0);
            StackScale.Set(ScaleTransform.ScaleXProperty, 0.94);
            StackScale.Set(ScaleTransform.ScaleYProperty, 0.94);
            StackShift.Set(TranslateTransform.YProperty, 10);
        }

        Show();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Query.Focus();
        Keyboard.Focus(Query);

        Stack.To(OpacityProperty, 1, 170);
        StackScale.To(ScaleTransform.ScaleXProperty, 1, 260, Motion.OutStrong);
        StackScale.To(ScaleTransform.ScaleYProperty, 1, 260, Motion.OutStrong);
        StackShift.To(TranslateTransform.YProperty, 0, 260, Motion.OutStrong);

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
            _settingsOpen = false;
            _capturingHotkey = false;
            Query.IsEnabled = true;
            SettingsOverlay.Visibility = Visibility.Collapsed;
            SettingsOverlay.Set(OpacityProperty, 0);

            Hide();
            Query.Text = string.Empty;
            Tiles.SelectedIndex = -1;
            HideRename(immediate: true);
            HideMessage(immediate: true);
            SetDropOverlay(false);
            Panel.Set(HeightProperty, TargetPanelHeight());
            App.Current.OnLauncherClosed();
        }

        if (!animated) { Finish(); return; }

        Stack.To(OpacityProperty, 0, 140, Motion.In, completed: Finish);
        StackScale.To(ScaleTransform.ScaleXProperty, 0.97, 140, Motion.In);
        StackScale.To(ScaleTransform.ScaleYProperty, 0.97, 140, Motion.In);
        StackShift.To(TranslateTransform.YProperty, 4, 140, Motion.In);
    }

    // ───────────────────────── Tiles ─────────────────────────

    private string CurrentQuery => Query.Text.Trim();

    private void Rebuild()
    {
        var q = CurrentQuery;
        var items = q.Length == 0 ? _ordered : ShortcutLibrary.Search(_ordered, q);

        _tiles.Clear();

        if (q.Length == 0 && _currentFolder is not null)
        {
            // Inside a folder: back tile + that folder's members.
            _tiles.Add(new BackTile(_currentFolder));
            foreach (var i in _ordered.Where(x => SameFolder(x.Folder, _currentFolder))) _tiles.Add(i);
            _tiles.Add(AddTile.Instance);
        }
        else
        {
            // Top level (or a search, which flattens folders so everything is findable).
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var item in items)
            {
                if (item.Folder is null)
                {
                    _tiles.Add(item);
                    continue;
                }
                if (q.Length == 0 && seen.Add(item.Folder))
                {
                    var members = items.Where(x => SameFolder(x.Folder, item.Folder)).ToList();
                    _tiles.Add(new FolderTile(item.Folder, members));
                }
                else if (q.Length > 0)
                {
                    _tiles.Add(item); // search: show the item itself, not the folder
                }
            }
            if (q.Length == 0 && _ordered.Count > 0) _tiles.Add(AddTile.Instance);
        }

        bool empty = _ordered.Count == 0;
        EmptyState.Visibility = empty && q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = EmptyState.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        bool noMatch = q.Length > 0 && items.Count == 0;
        NoMatch.Visibility = noMatch ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Text = noMatch ? $"没有找到 “{q}”" : string.Empty;
        ResultCount.Text = q.Length > 0 && items.Count > 0 ? items.Count.ToString() : string.Empty;

        if (q.Length > 0) Tiles.SelectedIndex = _tiles.Count > 0 ? 0 : -1;
        else Tiles.SelectedIndex = -1;
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
    }

    /// <summary>Applies the icon/text/spacing settings and keeps the open panel correctly sized.</summary>
    public void ApplyPanelStyle()
    {
        ApplyPanelMetrics();
        Rebuild();
        if (IsOpen)
        {
            Panel.Set(HeightProperty, TargetPanelHeight());
            Scroller.ScrollToTop();
        }
    }

    private double TargetPanelHeight()
    {
        if (_settingsOpen) return SettingsHeight;
        if (_tiles.Count == 0) return EmptyHeight;
        var rows = Math.Clamp((int)Math.Ceiling(_tiles.Count / (double)Cols), 1, MaxRowCount);
        return PanelChrome + rows * TileHeight;
    }

    private void AnimatePanelHeight()
    {
        var target = TargetPanelHeight();
        if (Math.Abs(Panel.ActualHeight - target) < 0.5) return;
        Panel.To(HeightProperty, target, 220, Motion.Out);
    }

    /// <summary>Rebuilds the tile grid for the configured columns/rows. Called when settings change.</summary>
    public void ApplyLayout()
    {
        var factory = new FrameworkElementFactory(typeof(UniformGrid));
        factory.SetValue(UniformGrid.ColumnsProperty, Cols);
        factory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
        Tiles.ItemsPanel = new ItemsPanelTemplate(factory);

        Rebuild();
        if (IsOpen)
        {
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

    /// <summary>Tiles fade/rise in. On open they ripple in quickly; on filter they just fade.</summary>
    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem container) return;
        var index = Tiles.ItemContainerGenerator.IndexFromContainer(container);
        var delay = _staggerNext ? Math.Min(index, 12) * 11 + 40 : 0;
        var shift = (TranslateTransform)EnsureTransforms(container).Children[1];

        container.FromTo(OpacityProperty, 0, 1, _staggerNext ? 200 : 130, Motion.Out, delay);
        if (_staggerNext)
        {
            shift.FromTo(TranslateTransform.YProperty, 6, 0, 240, Motion.OutStrong, delay);
        }
        if (_staggerNext && index >= _tiles.Count - 1)
            Dispatcher.BeginInvoke(() => _staggerNext = false, DispatcherPriority.ContextIdle);
    }

    /// <summary>While searching, the pointer drives the selection; in edit mode, a drag starts.</summary>
    private void OnTilesMouseMove(object sender, MouseEventArgs e)
    {
        if (_settingsOpen) return;

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
        if (ContainerFrom(e.OriginalSource) is { } c && !c.IsSelected) Tiles.SelectedItem = c.DataContext;
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
        if (_settingsOpen) return;
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
        }
    }

    private void OpenFolder(string? name)
    {
        _currentFolder = name;
        Rebuild();
        Panel.Set(HeightProperty, TargetPanelHeight());
        Scroller.ScrollToTop();
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

    private void OnQueryChanged()
    {
        if (!IsOpen) return;
        if (_editMode && Query.Text.Length > 0) ExitEditMode();
        if (Query.Text.Length > 0 && MessageRow.IsHitTestVisible) HideMessage();
        _staggerNext = false;
        Rebuild();
        AnimatePanelHeight();
        Scroller.ScrollToTop();
        UpdatePill();
    }

    private bool PillWanted => Query.Text.Length > 0 || _messageTimer.IsEnabled || MessageRow.IsHitTestVisible;

    private void UpdatePill()
    {
        var show = PillWanted;
        Pill.IsHitTestVisible = show;
        Pill.To(OpacityProperty, show ? 1 : 0, show ? 160 : 140, show ? Motion.Out : Motion.In);
        PillShift.To(TranslateTransform.YProperty, show ? 0 : 6, show ? 220 : 140, show ? Motion.OutStrong : Motion.In);

        bool message = MessageRow.IsHitTestVisible && Query.Text.Length == 0;
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
            Pill.Set(OpacityProperty, 0);
            Pill.IsHitTestVisible = false;
            PillShift.Set(TranslateTransform.YProperty, 6);
            return;
        }
        UpdatePill();
    }

    // ───────────────────────── Keyboard ─────────────────────────

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_renaming is not null || _renamingFolder is not null) return; // rename box handles its own keys
        if (_modalDepth > 0) return;
        if (_capturingHotkey) { HandleHotkeyCapture(e); return; }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (_settingsOpen)
        {
            if (e.Key == Key.Escape) { HideSettings(); e.Handled = true; }
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (_editMode) ExitEditMode();
                else if (Query.Text.Length > 0) Query.Text = string.Empty;
                else if (_currentFolder is not null) OpenFolder(null);
                else Close(animated: true);
                e.Handled = true;
                break;

            case Key.Back when Query.Text.Length == 0 && _currentFolder is not null:
                OpenFolder(null);
                e.Handled = true;
                break;

            case Key.Enter:
                if (Tiles.SelectedItem is { } selected) Invoke(selected, null);
                e.Handled = true;
                break;

            case Key.Left: MoveSelection(-1); e.Handled = true; break;
            case Key.Right: MoveSelection(1); e.Handled = true; break;
            case Key.Up: MoveSelection(-Cols); e.Handled = true; break;
            case Key.Down: MoveSelection(Cols); e.Handled = true; break;
            case Key.Tab: MoveSelection((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); e.Handled = true; break;

            case Key.F2:
                if (Tiles.SelectedItem is ShortcutItem r) BeginRename(r);
                e.Handled = true;
                break;

            case Key.Delete when Query.Text.Length == 0:
                if (Tiles.SelectedItem is ShortcutItem d) { Remove(d); e.Handled = true; }
                break;

            case Key.OemComma when ctrl:
                ShowSettings();
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

    private void MoveSelection(int delta)
    {
        if (_tiles.Count == 0) return;
        var i = Tiles.SelectedIndex;
        int next;
        if (i < 0) next = delta > 0 ? 0 : _tiles.Count - 1;
        else
        {
            next = i + delta;
            if (Math.Abs(delta) == 1) next = (next + _tiles.Count) % _tiles.Count;
            else next = Math.Clamp(next, 0, _tiles.Count - 1);
        }
        Tiles.SelectedIndex = next;
        if (Tiles.ItemContainerGenerator.ContainerFromIndex(next) is FrameworkElement c) c.BringIntoView();
    }

    // ───────────────────────── Context menu & editing ─────────────────────────

    private void OnTileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_settingsOpen) return;
        if (ContainerFrom(e.OriginalSource) is not { } c) return;
        e.Handled = true;

        switch (c.DataContext)
        {
            case ShortcutItem item: ShowShortcutMenu(item, c); break;
            case FolderTile folder: ShowFolderMenu(folder, c); break;
            case AddTile: ShowAddMenu(c); break;
            case BackTile: OpenFolder(null); break;
        }
    }

    private void ShowFolderMenu(FolderTile folder, FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
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

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(Item("打开", () => OpenItem(item), "Enter"));
        if (item.Kind is ShortcutKind.File or ShortcutKind.Folder or ShortcutKind.App)
            menu.Items.Add(Item("打开所在位置", () => { ShellLauncher.Reveal(item); Close(animated: true); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(item.Pinned ? "取消固定" : "固定在最前", () => TogglePin(item)));
        menu.Items.Add(Item("重命名", () => BeginRename(item), "F2"));
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

    public void ShowSettings()
    {
        if (!IsOpen) Open();
        if (_settingsOpen) { RefreshSettings(); return; }

        _settingsOpen = true;
        _editMode = false;
        _capturingHotkey = false;
        Query.Text = string.Empty;
        Query.IsEnabled = false;
        HideMessage(immediate: true);
        HideRename(immediate: true);

        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.To(OpacityProperty, 1, 160, Motion.Out);
        Panel.To(HeightProperty, SettingsHeight, 260, Motion.OutStrong);
        _staggerNext = false;

        RefreshSettings();
        Keyboard.Focus(HotkeyBox);
    }

    private void HideSettings(bool immediate = false)
    {
        if (!_settingsOpen) return;
        _settingsOpen = false;
        _capturingHotkey = false;
        HotkeyHint.Text = App.Current.Settings.HotkeyEnabled
            ? HotkeyHintDefault
            : "默认关闭；打开左侧开关后生效，点击这里可先改组合键";
        Query.IsEnabled = true;

        if (immediate)
        {
            SettingsOverlay.Set(OpacityProperty, 0);
            SettingsOverlay.Visibility = Visibility.Collapsed;
            Panel.Set(HeightProperty, TargetPanelHeight());
        }
        else
        {
            SettingsOverlay.To(OpacityProperty, 0, 130, Motion.In, completed: () =>
            {
                if (!_settingsOpen) SettingsOverlay.Visibility = Visibility.Collapsed;
            });
            Panel.To(HeightProperty, TargetPanelHeight(), 240, Motion.Out);
        }

        if (IsOpen)
        {
            Query.Focus();
            Keyboard.Focus(Query);
            if (_pendingNotice is { } notice)
            {
                _pendingNotice = null;
                ShowMessage(notice);
            }
        }
    }

    private void RefreshSettings()
    {
        var s = App.Current.Settings;

        ThemeSystem.IsChecked = s.Theme == AppTheme.System;
        ThemeLight.IsChecked = s.Theme == AppTheme.Light;
        ThemeDark.IsChecked = s.Theme == AppTheme.Dark;

        ColumnsValue.Text = s.Columns.ToString();
        RowsValue.Text = s.MaxRows.ToString();
        ColumnsMinus.IsEnabled = s.Columns > 3;
        ColumnsPlus.IsEnabled = s.Columns < 5;
        RowsMinus.IsEnabled = s.MaxRows > 2;
        RowsPlus.IsEnabled = s.MaxRows < 5;

        IconValue.Text = s.PanelIconSize.ToString();
        FontValue.Text = s.PanelFontSize.ToString();
        GapValue.Text = s.PanelGap.ToString();
        IconMinus.IsEnabled = s.PanelIconSize > 32;
        IconPlus.IsEnabled = s.PanelIconSize < 48;
        FontMinus.IsEnabled = s.PanelFontSize > 10;
        FontPlus.IsEnabled = s.PanelFontSize < 14;
        GapMinus.IsEnabled = s.PanelGap > 2;
        GapPlus.IsEnabled = s.PanelGap < 10;

        DockVisibleSwitch.IsChecked = s.ShowDock;
        DockSizeValue.Text = $"{s.DockSize} px";
        DockSizeMinus.IsEnabled = s.DockSize > 32;
        DockSizePlus.IsEnabled = s.DockSize < 64;

        PeekValue.Text = $"{s.PeekDelayMs / 1000.0:0.0} 秒";
        PeekMinus.IsEnabled = s.PeekDelayMs > 600;
        PeekPlus.IsEnabled = s.PeekDelayMs < 8000;

        _settingsRefreshing = true;
        ColorWhite.IsChecked = string.Equals(s.DockColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase);
        ColorGraphite.IsChecked = string.Equals(s.DockColor, "#2B2F36", StringComparison.OrdinalIgnoreCase);
        ColorGray.IsChecked = string.Equals(s.DockColor, "#D1D5DB", StringComparison.OrdinalIgnoreCase);
        ColorBlue.IsChecked = string.Equals(s.DockColor, "#3B82F6", StringComparison.OrdinalIgnoreCase);
        ColorGreen.IsChecked = string.Equals(s.DockColor, "#22C55E", StringComparison.OrdinalIgnoreCase);
        ColorOrange.IsChecked = string.Equals(s.DockColor, "#F59E0B", StringComparison.OrdinalIgnoreCase);
        ColorRose.IsChecked = string.Equals(s.DockColor, "#F43F5E", StringComparison.OrdinalIgnoreCase);
        _settingsRefreshing = false;

        HotkeyText.Text = s.Hotkey;
        HotkeySwitch.IsChecked = s.HotkeyEnabled;
        AutostartSwitch.IsChecked = StartupService.IsEnabled;
        if (!_capturingHotkey)
            HotkeyHint.Text = s.HotkeyEnabled
                ? HotkeyHintDefault
                : "默认关闭；打开左侧开关后生效，点击这里可先改组合键";

        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version is null ? "Jump" : $"Jump v{version.ToString(3)}";
    }

    private void OnThemeSystem(object sender, RoutedEventArgs e) => ApplyThemeFromSettings(AppTheme.System);
    private void OnThemeLight(object sender, RoutedEventArgs e) => ApplyThemeFromSettings(AppTheme.Light);
    private void OnThemeDark(object sender, RoutedEventArgs e) => ApplyThemeFromSettings(AppTheme.Dark);

    private void ApplyThemeFromSettings(AppTheme mode)
    {
        App.Current.SetTheme(mode);
        RefreshSettings();
    }

    private void OnColumnsMinus(object sender, RoutedEventArgs e) => ChangeColumns(-1);
    private void OnColumnsPlus(object sender, RoutedEventArgs e) => ChangeColumns(1);
    private void OnRowsMinus(object sender, RoutedEventArgs e) => ChangeRows(-1);
    private void OnRowsPlus(object sender, RoutedEventArgs e) => ChangeRows(1);
    private void OnPeekMinus(object sender, RoutedEventArgs e) => ChangePeek(-400);
    private void OnPeekPlus(object sender, RoutedEventArgs e) => ChangePeek(400);

    private void OnIconMinus(object sender, RoutedEventArgs e) => ChangePanelIcon(-4);
    private void OnIconPlus(object sender, RoutedEventArgs e) => ChangePanelIcon(4);
    private void OnFontMinus(object sender, RoutedEventArgs e) => ChangePanelFont(-1);
    private void OnFontPlus(object sender, RoutedEventArgs e) => ChangePanelFont(1);
    private void OnGapMinus(object sender, RoutedEventArgs e) => ChangePanelGap(-2);
    private void OnGapPlus(object sender, RoutedEventArgs e) => ChangePanelGap(2);

    private void OnDockSizeMinus(object sender, RoutedEventArgs e) => ChangeDockSize(-4);
    private void OnDockSizePlus(object sender, RoutedEventArgs e) => ChangeDockSize(4);

    private void OnHotkeyEnabledClick(object sender, RoutedEventArgs e)
    {
        var want = HotkeySwitch.IsChecked == true;
        var ok = App.Current.SetHotkeyEnabled(want, out var error);
        RefreshSettings();
        if (!ok)
        {
            HotkeySwitch.IsChecked = !want;
            HotkeyHint.Text = error ?? "无法启用该快捷键";
        }
    }

    private void OnDockVisibleClick(object sender, RoutedEventArgs e)
    {
        App.Current.SetShowDock(DockVisibleSwitch.IsChecked == true);
        RefreshSettings();
    }

    private void OnDockColorChecked(object sender, RoutedEventArgs e)
    {
        if (_settingsRefreshing) return;
        if (sender is RadioButton { Tag: string color })
        {
            App.Current.SetDockColor(color);
            RefreshSettings();
        }
    }

    private void ChangePanelIcon(int delta)
    {
        App.Current.SetPanelIconSize(App.Current.Settings.PanelIconSize + delta);
        RefreshSettings();
    }

    private void ChangePanelFont(int delta)
    {
        App.Current.SetPanelFontSize(App.Current.Settings.PanelFontSize + delta);
        RefreshSettings();
    }

    private void ChangePanelGap(int delta)
    {
        App.Current.SetPanelGap(App.Current.Settings.PanelGap + delta);
        RefreshSettings();
    }

    private void ChangeDockSize(int delta)
    {
        App.Current.SetDockSize(App.Current.Settings.DockSize + delta);
        RefreshSettings();
    }

    private void ChangeColumns(int delta)
    {
        App.Current.SetColumns(App.Current.Settings.Columns + delta);
        RefreshSettings();
    }

    private void ChangeRows(int delta)
    {
        App.Current.SetMaxRows(App.Current.Settings.MaxRows + delta);
        RefreshSettings();
    }

    private void ChangePeek(int delta)
    {
        App.Current.SetPeekDelay(App.Current.Settings.PeekDelayMs + delta);
        RefreshSettings();
    }

    private void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        var want = AutostartSwitch.IsChecked == true;
        var error = App.Current.SetAutostart(want);
        if (error is not null)
        {
            AutostartSwitch.IsChecked = !want;
            ShowMessage($"无法修改开机启动：{error}");
        }
        RefreshSettings();
    }

    private void OnSettingsDone(object sender, RoutedEventArgs e) => HideSettings();
    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => App.Current.OpenDataFolder();

    private void OnHotkeyBoxClick(object sender, MouseButtonEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyText.Text = "按下组合键…";
        HotkeyHint.Text = "Esc 取消 · 至少需要一个修饰键";
        Keyboard.Focus(HotkeyBox);
        e.Handled = true;
    }

    private void HandleHotkeyCapture(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Modifier-only presses are not a complete shortcut.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab)
            return;

        if (key == Key.Escape)
        {
            EndHotkeyCapture();
            HotkeyHint.Text = "已取消";
            return;
        }

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            HotkeyHint.Text = "至少需要 Ctrl / Alt / Shift / Win 中的一个";
            return;
        }

        var text = FormatHotkey(mods, key);
        if (App.Current.TrySetHotkey(text, out var error))
        {
            EndHotkeyCapture();
            HotkeyHint.Text = App.Current.Settings.HotkeyEnabled
                ? $"已更新为 {text}"
                : $"已设置为 {text}；打开开关后生效";
        }
        else
        {
            HotkeyHint.Text = error ?? "无法注册该快捷键";
        }
    }

    private void EndHotkeyCapture()
    {
        _capturingHotkey = false;
        RefreshSettings();
        Keyboard.Focus(HotkeyBox);
    }

    private void CancelHotkeyCapture()
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        HotkeyHint.Text = App.Current.Settings.HotkeyEnabled
            ? HotkeyHintDefault
            : "默认关闭；打开左侧开关后生效，点击这里可先改组合键";
        HotkeyText.Text = App.Current.Settings.Hotkey;
        Query.IsEnabled = false;
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var d = node; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private static string FormatHotkey(ModifierKeys mods, Key key)
    {
        var parts = new List<string>(5);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key)
    {
        // KeyConverter round-trips through HotkeyService.TryParse for letters, digits,
        // arrows, OEM punctuation and numpad keys alike.
        var text = new KeyConverter().ConvertToInvariantString(key);
        return string.IsNullOrWhiteSpace(text) ? key.ToString() : text;
    }

    /// <summary>Shows a notice immediately when open, or on the next open otherwise.</summary>
    public void ShowNotice(string text)
    {
        if (IsOpen && !_settingsOpen) ShowMessage(text);
        else _pendingNotice = text;
    }

    // ───────────────────────── Adding ─────────────────────────

    private void ShowAddMenu(FrameworkElement? anchor)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor ?? (FrameworkElement)Panel,
            Placement = anchor is null
                ? System.Windows.Controls.Primitives.PlacementMode.Center
                : System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };
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
        if (!IsOpen) return;
        // New items join at the end of the current order instead of reshuffling what you see.
        var current = new HashSet<ShortcutItem>(_library.Items);
        var kept = _ordered.Where(current.Contains).ToList();
        foreach (var i in _library.Items) if (!kept.Contains(i)) kept.Add(i);
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
        if (HasInternalTile(e)) { e.Effects = DragDropEffects.Move; return; }
        if (!HasDroppable(e)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Link | DragDropEffects.Copy;
        SetDropOverlay(true);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (HasInternalTile(e)) { e.Effects = DragDropEffects.Move; e.Handled = true; return; }
        e.Effects = HasDroppable(e) ? DragDropEffects.Link | DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; only hide when truly outside.
        var p = e.GetPosition(Panel);
        if (p.X > 0 && p.Y > 0 && p.X < Panel.ActualWidth && p.Y < Panel.ActualHeight) return;
        SetDropOverlay(false);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(InternalTileFormat) is { } raw)
        {
            HandleInternalDrop(raw, e);
            return;
        }

        SetDropOverlay(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddPaths(files);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text)
        {
            var items = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ShellLauncher.FromInput).OfType<ShortcutItem>().ToList();
            if (items.Count > 0) Commit(items);
        }
        Activate();
        Keyboard.Focus(Query);
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
        ShowStickyMessage("编辑模式 · 拖一个磁贴到另一个上即可合成分组", "完成", ExitEditMode);
    }

    private void ExitEditMode()
    {
        if (!_editMode) return;
        _editMode = false;
        _dragArmed = false;
        HideMessage(immediate: true);
        Rebuild();
    }

    private void HandleInternalDrop(object raw, DragEventArgs e)
    {
        if (raw is not ShortcutItem dragged) return;
        var pos = e.GetPosition(Tiles);
        var container = FindContainerAt(pos);
        var target = container?.DataContext;

        if (target is ShortcutItem targetItem && !ReferenceEquals(targetItem, dragged) && container is not null)
        {
            // Left/right edge → reorder; centre → merge into a folder.
            var local = e.GetPosition(container);
            var rel = local.X / Math.Max(1, container.ActualWidth);
            if (rel < 0.32)
            {
                _library.Reorder(dragged, targetItem, after: false);
                RefreshOrder();
            }
            else if (rel > 0.68)
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
            MoveToFolder(dragged, folder.Name);
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
        ShowStickyMessage("已调整顺序", "完成", ExitEditMode);
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
        ShowStickyMessage($"已整理到「{name}」· 右键分组可重命名", "完成", ExitEditMode);
    }

    private void MoveToFolder(ShortcutItem item, string folderName)
    {
        item.Folder = folderName;
        _library.SaveSoon();
        _ordered = _library.Ordered();
        Rebuild();
        AnimatePanelHeight();
        ShowStickyMessage($"已加入「{folderName}」", "完成", ExitEditMode);
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
