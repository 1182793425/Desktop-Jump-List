using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
}

/// <summary>
/// The centred panel. Nothing but your shortcuts: a grid of tiles, type-to-filter, click to open.
/// The search pill only exists while you are typing (or for a brief message).
/// </summary>
public partial class LauncherWindow : Window
{
    private const int Columns = 5;
    private const int MaxRows = 4;
    private const double TileHeight = 96;
    private const double PanelChrome = 2 + 28; // border + inner padding
    private const double EmptyHeight = 210;

    private readonly ShortcutLibrary _library;
    private readonly ObservableCollection<object> _tiles = new();
    private readonly DispatcherTimer _messageTimer;

    private List<ShortcutItem> _ordered = new();
    private bool _closing;
    private bool _staggerNext;
    private int _modalDepth;
    private ShortcutItem? _renaming;
    private (ShortcutItem item, int index)? _lastRemoved;
    private Action? _messageAction;

    public bool IsOpen { get; private set; }

    public LauncherWindow(ShortcutLibrary library)
    {
        InitializeComponent();
        _library = library;
        Tiles.ItemsSource = _tiles;

        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _messageTimer.Tick += (_, _) => HideMessage();

        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, noActivate: false);
        new WindowInteropHelper(this).EnsureHandle(); // first open is as fast as every other

        Deactivated += (_, _) => { if (IsOpen && _modalDepth == 0) Close(animated: true); };
        PreviewKeyDown += OnKey;
        Query.TextChanged += (_, _) => OnQueryChanged();

        Tiles.PreviewMouseMove += OnTilesMouseMove;
        Tiles.AddHandler(ListBoxItem.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnTileDown), true);
        Tiles.AddHandler(ListBoxItem.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnTileUp), true);
        Tiles.AddHandler(ListBoxItem.MouseRightButtonUpEvent, new MouseButtonEventHandler(OnTileRightClick), true);

        EmptyState.MouseLeftButtonUp += (_, _) => AddViaDialog(folder: false);
        MessageAction.Click += (_, _) => { var a = _messageAction; HideMessage(); a?.Invoke(); };
        RenameBox.PreviewKeyDown += OnRenameKey;

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

        _library.Items.CollectionChanged += OnLibraryChanged;
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
            Query.Text = string.Empty;
            HideMessage(immediate: true);
            HideRename(immediate: true);
            _ordered = _library.Ordered();
            _staggerNext = true;
            Rebuild();
            Panel.Set(HeightProperty, TargetPanelHeight());
            Scroller.ScrollToTop();

            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2 - 24; // optical centre sits a little high

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
            Hide();
            Query.Text = string.Empty;
            Tiles.SelectedIndex = -1;
            HideRename(immediate: true);
            HideMessage(immediate: true);
            SetDropOverlay(false);
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
        foreach (var i in items) _tiles.Add(i);
        if (q.Length == 0 && _ordered.Count > 0) _tiles.Add(AddTile.Instance);

        bool empty = _ordered.Count == 0;
        EmptyState.Visibility = empty && q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = EmptyState.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        bool noMatch = q.Length > 0 && items.Count == 0;
        NoMatch.Visibility = noMatch ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Text = noMatch ? $"没有找到 “{q}”" : string.Empty;
        ResultCount.Text = q.Length > 0 && items.Count > 0 ? items.Count.ToString() : string.Empty;

        if (q.Length > 0) Tiles.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    private double TargetPanelHeight()
    {
        if (_ordered.Count == 0) return EmptyHeight;
        var count = _ordered.Count + 1; // + the add tile
        var rows = Math.Clamp((int)Math.Ceiling(count / (double)Columns), 1, MaxRows);
        return PanelChrome + rows * TileHeight;
    }

    private void AnimatePanelHeight()
    {
        var target = TargetPanelHeight();
        if (Math.Abs(Panel.ActualHeight - target) < 0.5) return;
        Panel.To(HeightProperty, target, 220, Motion.Out);
    }

    /// <summary>Tiles fade/rise in. On open they ripple in quickly; on filter they just fade.</summary>
    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem container) return;
        var index = Tiles.ItemContainerGenerator.IndexFromContainer(container);
        var delay = _staggerNext ? Math.Min(index, 12) * 11 + 40 : 0;
        var shift = (TranslateTransform)((TransformGroup)container.RenderTransform).Children[1];

        container.FromTo(OpacityProperty, 0, 1, _staggerNext ? 200 : 130, Motion.Out, delay);
        if (_staggerNext)
        {
            shift.FromTo(TranslateTransform.YProperty, 6, 0, 240, Motion.OutStrong, delay);
        }
        if (_staggerNext && index >= _tiles.Count - 1)
            Dispatcher.BeginInvoke(() => _staggerNext = false, DispatcherPriority.ContextIdle);
    }

    /// <summary>While searching, the pointer drives the same selection that Enter opens.</summary>
    private void OnTilesMouseMove(object sender, MouseEventArgs e)
    {
        if (Query.Text.Length == 0) return;
        if (ContainerFrom(e.OriginalSource) is { } c && !c.IsSelected) Tiles.SelectedItem = c.DataContext;
    }

    private static ListBoxItem? ContainerFrom(object source)
    {
        var d = source as DependencyObject;
        while (d is not null and not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }

    private void OnTileDown(object sender, MouseButtonEventArgs e)
    {
        if (ContainerFrom(e.OriginalSource) is not { } c) return;
        var scale = (ScaleTransform)((TransformGroup)c.RenderTransform).Children[0];
        scale.To(ScaleTransform.ScaleXProperty, 0.94, 90);
        scale.To(ScaleTransform.ScaleYProperty, 0.94, 90);
        e.Handled = true;
    }

    private void OnTileUp(object sender, MouseButtonEventArgs e)
    {
        if (ContainerFrom(e.OriginalSource) is not { } c) return;
        var scale = (ScaleTransform)((TransformGroup)c.RenderTransform).Children[0];
        scale.To(ScaleTransform.ScaleXProperty, 1, 180, Motion.OutStrong);
        scale.To(ScaleTransform.ScaleYProperty, 1, 180, Motion.OutStrong);
        e.Handled = true;
        if (!c.IsMouseOver) return;
        Invoke(c.DataContext, c);
    }

    private void Invoke(object? data, FrameworkElement? anchor)
    {
        switch (data)
        {
            case ShortcutItem item: OpenItem(item); break;
            case AddTile: ShowAddMenu(anchor); break;
        }
    }

    private void OpenItem(ShortcutItem item)
    {
        var error = ShellLauncher.Open(item);
        if (error is not null)
        {
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
        if (Query.Text.Length > 0 && MessageRow.IsHitTestVisible) HideMessage();
        _staggerNext = false;
        Rebuild();
        Scroller.ScrollToTop();
        UpdatePill();
    }

    private bool PillWanted => Query.Text.Length > 0 || _messageTimer.IsEnabled || MessageRow.IsHitTestVisible;

    private void UpdatePill()
    {
        var show = PillWanted;
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
            PillShift.Set(TranslateTransform.YProperty, 6);
            return;
        }
        UpdatePill();
    }

    // ───────────────────────── Keyboard ─────────────────────────

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_renaming is not null) return; // rename box handles its own keys

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                if (Query.Text.Length > 0) Query.Text = string.Empty;
                else Close(animated: true);
                e.Handled = true;
                break;

            case Key.Enter:
                if (Tiles.SelectedItem is not null) Invoke(Tiles.SelectedItem, null);
                else if (_tiles.FirstOrDefault() is ShortcutItem first) OpenItem(first);
                e.Handled = true;
                break;

            case Key.Left: MoveSelection(-1); e.Handled = true; break;
            case Key.Right: MoveSelection(1); e.Handled = true; break;
            case Key.Up: MoveSelection(-Columns); e.Handled = true; break;
            case Key.Down: MoveSelection(Columns); e.Handled = true; break;
            case Key.Tab: MoveSelection((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); e.Handled = true; break;

            case Key.F2:
                if (Tiles.SelectedItem is ShortcutItem r) BeginRename(r);
                e.Handled = true;
                break;

            case Key.Delete when Query.Text.Length == 0:
                if (Tiles.SelectedItem is ShortcutItem d) { Remove(d); e.Handled = true; }
                break;

            case Key.V when ctrl && Query.Text.Length == 0:
                if (TryPasteAdd()) e.Handled = true;
                break;

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
        if (ContainerFrom(e.OriginalSource) is not { } c || c.DataContext is not ShortcutItem item) return;
        e.Handled = true;
        Tiles.SelectedItem = item;

        var menu = new ContextMenu { PlacementTarget = c, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(Item("打开", () => OpenItem(item), "Enter"));
        if (item.Kind != ShortcutKind.Url)
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
            if (_renaming is not null && name.Length > 0)
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
        if (immediate)
        {
            RenameOverlay.Set(OpacityProperty, 0);
            RenameOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        RenameOverlay.To(OpacityProperty, 0, 130, Motion.In, completed: () =>
        {
            if (_renaming is null) RenameOverlay.Visibility = Visibility.Collapsed;
        });
        Keyboard.Focus(Query);
    }

    // ───────────────────────── Adding ─────────────────────────

    private void ShowAddMenu(FrameworkElement? anchor)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor ?? (FrameworkElement)Panel,
            Placement = anchor is null ? System.Windows.Controls.Primitives.PlacementMode.Center : System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        menu.Items.Add(Item("选择文件或应用…", () => AddViaDialog(folder: false)));
        menu.Items.Add(Item("选择文件夹…", () => AddViaDialog(folder: true)));
        var paste = Item("粘贴路径或网址", () => { if (!TryPasteAdd()) ShowMessage("剪贴板里没有可添加的路径或网址"); }, "Ctrl+V");
        menu.Items.Add(paste);
        menu.Closed += (_, _) => { if (IsOpen) Keyboard.Focus(Query); };
        menu.IsOpen = true;
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

    private bool TryPasteAdd()
    {
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
                if (items.Count == 0) return false;
                Commit(items);
                return true;
            }
        }
        catch { }
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

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!HasDroppable(e)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Link | DragDropEffects.Copy;
        SetDropOverlay(true);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
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
}
