using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DesktopJumpList.Models;
using DesktopJumpList.Services;
using Forms = System.Windows.Forms;

namespace DesktopJumpList;

public partial class MainWindow : Window
{
    private readonly ShortcutStore _store = new();
    private readonly StartupService _startup = new();
    private readonly ObservableCollection<ShortcutItem> _shortcuts = [];
    private readonly ICollectionView _shortcutView;
    private readonly Forms.NotifyIcon _trayIcon;
    private bool _isExpanded;
    private bool _isDragging;
    private System.Windows.Point _dragStart;
    private bool _orbPressed;

    public MainWindow()
    {
        InitializeComponent();
        _shortcutView = CollectionViewSource.GetDefaultView(_shortcuts);
        _shortcutView.Filter = FilterShortcut;
        ShortcutList.ItemsSource = _shortcutView;
        _trayIcon = CreateTrayIcon();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var shortcut in await _store.LoadAsync()) _shortcuts.Add(shortcut);
        PlaceAtScreenEdge();
        SetCompactSize();
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示快捷面板", null, (_, _) => Dispatcher.Invoke(ShowPanel));
        var startupItem = new Forms.ToolStripMenuItem("开机启动") { Checked = _startup.IsEnabled(), CheckOnClick = true };
        startupItem.CheckedChanged += (_, _) => _startup.SetEnabled(startupItem.Checked);
        menu.Items.Add(startupItem);
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown()));

        var icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Desktop Jump List",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowPanel);
        return icon;
    }

    private void PlaceAtScreenEdge()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = Math.Max(workArea.Top + 100, (workArea.Top + workArea.Bottom - Height) / 2);
    }

    private void Orb_Click(object sender, RoutedEventArgs e)
    {
        if (_isDragging) { _isDragging = false; return; }
        if (_isExpanded) HidePanel(); else ShowPanel();
    }

    private void Orb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _orbPressed = true;
        _isDragging = false;
        Orb.CaptureMouse();
    }

    private void Orb_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_orbPressed || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        if (!_isDragging && (Math.Abs(position.X - _dragStart.X) > 4 || Math.Abs(position.Y - _dragStart.Y) > 4))
        {
            _isDragging = true;
            DragMove();
        }
    }

    private void Orb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _orbPressed = false;
        Orb.ReleaseMouseCapture();
        SnapToEdge();
    }

    private void SnapToEdge()
    {
        if (_isExpanded) return;
        var workArea = SystemParameters.WorkArea;
        Left = Left + Width / 2 < workArea.Left + workArea.Width / 2
            ? workArea.Left + 12
            : workArea.Right - Width - 12;
        Top = Math.Clamp(Top, workArea.Top + 12, workArea.Bottom - Height - 12);
    }

    private void ShowPanel()
    {
        if (_isExpanded) { Activate(); return; }
        _isExpanded = true;
        SetExpandedSize();
        Panel.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Panel.BeginAnimation(OpacityProperty, fade);
        Activate();
        SearchBox.Focus();
    }

    private void HidePanel()
    {
        if (!_isExpanded) return;
        _isExpanded = false;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            Panel.Visibility = Visibility.Collapsed;
            SetCompactSize();
        };
        Panel.BeginAnimation(OpacityProperty, fade);
    }

    private void SetCompactSize()
    {
        var right = Left + Width;
        Width = 58;
        Height = 58;
        Left = right - Width;
    }

    private void SetExpandedSize()
    {
        var right = Left + Width;
        Width = 360;
        Height = 600;
        Left = Math.Max(SystemParameters.WorkArea.Left + 12, right - Width);
        Top = Math.Min(Top, SystemParameters.WorkArea.Bottom - Height - 12);
    }

    private void ClosePanel_Click(object sender, RoutedEventArgs e) => HidePanel();

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isExpanded && !IsKeyboardFocusWithin) HidePanel();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == RootGrid && e.LeftButton == MouseButtonState.Pressed)
        {
            _isDragging = true;
            DragMove();
        }
    }

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Panel) DragMove();
    }

    private bool FilterShortcut(object item)
    {
        if (item is not ShortcutItem shortcut) return false;
        var term = SearchBox?.Text?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(term)
            || shortcut.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || shortcut.Group.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _shortcutView.Refresh();

    private void OpenShortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not ShortcutItem shortcut) return;
        try { ShellLauncher.Open(shortcut); HidePanel(); }
        catch (Exception ex) { System.Windows.MessageBox.Show($"无法打开“{shortcut.Name}”。\n\n{ex.Message}", "Desktop Jump List", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void AddShortcut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddShortcutWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Shortcut is null) return;
        dialog.Shortcut.SortOrder = _shortcuts.Count;
        _shortcuts.Add(dialog.Shortcut);
        await SaveAsync();
    }

    private void ShortcutMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is not ShortcutItem shortcut) return;
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = "编辑" };
        edit.Click += async (_, _) =>
        {
            var dialog = new AddShortcutWindow(shortcut) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Shortcut is not null)
            {
                var index = _shortcuts.IndexOf(shortcut);
                _shortcuts[index] = dialog.Shortcut;
                await SaveAsync();
                _shortcutView.Refresh();
            }
        };
        var delete = new MenuItem { Header = "删除" };
        delete.Click += async (_, _) =>
        {
            if (System.Windows.MessageBox.Show($"删除“{shortcut.Name}”？", "Desktop Jump List", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _shortcuts.Remove(shortcut);
            await SaveAsync();
        };
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private Task SaveAsync() => _store.SaveAsync(_shortcuts);

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }
}
