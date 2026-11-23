using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopJumpList.Models;
using DesktopJumpList.Services;
using DesktopJumpList.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DesktopJumpList;

public partial class MainWindow : Window
{
    private const double DockSize = 60;
    private const double PanelWidth = 432;
    private const double PanelHeight = 648;
    private const double ShadowMargin = 32;
    private const double EdgeInset = 12;

    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly string? _shotPath;
    private readonly string _previewView;
    private readonly HotkeyService _hotkey = new();

    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private bool _initialized;
    private bool _panelOpen;
    private bool _dragPressed;
    private bool _dragMoved;
    private int _modalDepth;
    private bool _anchorRight = true;
    private Drawing.Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;

    public MainWindow(
        MainViewModel viewModel,
        AppSettings settings,
        string? shotPath,
        string previewView)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _shotPath = shotPath;
        _previewView = previewView;
        DataContext = viewModel;
        _viewModel.CloseRequested += HidePanel;
        _viewModel.ExitRequested += ExitApplication;

        Width = DockSize;
        Height = DockSize;
        var area = SystemParameters.WorkArea;
        _anchorRight = !string.Equals(_settings.Edge, "Left", StringComparison.OrdinalIgnoreCase);
        Left = _anchorRight ? area.Right - DockSize - EdgeInset : area.Left + EdgeInset;
        Top = area.Top + Math.Max(0, area.Height - DockSize) * _settings.TopRatio;

        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Dock.MouseEnter += (_, _) => AnimateDockScale(1.035);
        Dock.MouseLeave += (_, _) =>
        {
            if (!_dragPressed) AnimateDockScale(1);
        };
    }

    public void ShowPanel() => ShowPanel(center: false, animate: true);

    public void ShowPanel(bool center, bool animate)
    {
        App.Log($"ShowPanel requested open={_panelOpen} center={center}");
        if (_panelOpen)
        {
            Activate();
            if (_viewModel.IsEditorOpen) Panel.FocusEditor();
            else if (_viewModel.IsSettingsOpen) Panel.FocusSettings();
            else if (!_viewModel.IsInsightsOpen) Panel.FocusSearch();
            return;
        }
        var area = GetWorkArea();
        var panelWidth = Math.Min(PanelWidth, Math.Max(320, area.Width - 24));
        var panelHeight = Math.Min(PanelHeight, Math.Max(420, area.Height - 24));
        var dockRect = new Rect(Left, Top, DockSize, DockSize);
        double panelLeft;
        double panelTop;
        if (center)
        {
            panelLeft = area.Left + (area.Width - panelWidth) / 2;
            panelTop = area.Top + (area.Height - panelHeight) / 2;
        }
        else
        {
            _anchorRight = dockRect.Left + dockRect.Width / 2 >= area.Left + area.Width / 2;
            panelLeft = _anchorRight ? dockRect.Right - panelWidth : dockRect.Left;
            panelTop = Math.Clamp(dockRect.Top + dockRect.Height / 2 - panelHeight / 2,
                area.Top + 8, area.Bottom - panelHeight - 8);
        }

        _panelOpen = true;
        Dock.Visibility = Visibility.Collapsed;
        Width = panelWidth + ShadowMargin * 2;
        Height = panelHeight + ShadowMargin * 2;
        Left = panelLeft - ShadowMargin;
        Top = panelTop - ShadowMargin;

        PanelHost.Visibility = Visibility.Visible;
        PanelHost.BeginAnimation(OpacityProperty, null);
        PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!animate)
        {
            PanelHost.Opacity = 1;
            PanelScale.ScaleX = PanelScale.ScaleY = 1;
        }
        else
        {
            PanelHost.Opacity = 0;
            PanelScale.ScaleX = PanelScale.ScaleY = 0.975;
            PanelHost.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.975, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.975, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = ease });
        }

        Activate();
        if (_viewModel.IsEditorOpen) Panel.FocusEditor();
        else if (_viewModel.IsSettingsOpen) Panel.FocusSettings();
        else if (!_viewModel.IsInsightsOpen) Panel.FocusSearch();
    }

    public void HidePanel()
    {
        App.Log($"HidePanel requested open={_panelOpen}");
        if (!_panelOpen) return;
        _panelOpen = false;
        _viewModel.SearchText = string.Empty;
        if (_viewModel.IsInsightsOpen || _viewModel.IsEditorOpen || _viewModel.IsSettingsOpen)
            _viewModel.CloseOverlaysCommand.Execute(null);
        Panel.ResetState();
        var area = GetWorkArea();
        var contentRect = new Rect(Left + ShadowMargin, Top + ShadowMargin, Width - ShadowMargin * 2, Height - ShadowMargin * 2);
        var dockTop = Math.Clamp(contentRect.Top + contentRect.Height / 2 - DockSize / 2,
            area.Top + 8, area.Bottom - DockSize - 8);
        var targetLeft = _anchorRight ? area.Right - DockSize - EdgeInset : area.Left + EdgeInset;

        var fade = new DoubleAnimation(PanelHost.Opacity, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (_panelOpen) return;
            PanelHost.Visibility = Visibility.Collapsed;
            Dock.Visibility = Visibility.Visible;
            Width = DockSize;
            Height = DockSize;
            Left = targetLeft;
            Top = dockTop;
            Dock.BeginAnimation(OpacityProperty, null);
            Dock.Opacity = 0;
            Dock.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            AnimateDockScale(1, 170, fromCurrent: true);
            SavePlacement(_anchorRight ? "Right" : "Left", (dockTop - area.Top) / Math.Max(1, area.Height - DockSize));
        };
        PanelHost.BeginAnimation(OpacityProperty, fade);
    }

    public void BeginModal() => _modalDepth++;

    public void EndModal()
    {
        _modalDepth = Math.Max(0, _modalDepth - 1);
        if (_modalDepth != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Activate();
            Panel.FocusEditor();
        }, DispatcherPriority.Input);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        App.Log($"OnLoaded preview={App.IsPreview} shortcuts={_viewModel.Shortcuts.Count}");
        await _viewModel.InitializeAsync();
        App.Log($"Initialized shortcuts={_viewModel.Shortcuts.Count}");

        if (App.IsPreview)
        {
            await RunPreviewAsync();
            return;
        }

        PlaceDock(GetWorkArea());
        CreateTrayIcon();
        RegisterHotkey();
        WindowFadeIn();
    }

    private void RegisterHotkey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (string.IsNullOrWhiteSpace(_settings.Hotkey)) return;
        _viewModel.HotkeyAvailable = _hotkey.Register(handle, _settings.Hotkey, ToggleFromHotkey);
    }

    private void ToggleFromHotkey()
    {
        App.Log($"Hotkey toggled open={_panelOpen}");
        if (_panelOpen) HidePanel();
        else ShowPanel();
    }

    private void PlaceDock(Rect area)
    {
        Width = DockSize;
        Height = DockSize;
        _anchorRight = !string.Equals(_settings.Edge, "Left", StringComparison.OrdinalIgnoreCase);
        var ratio = _settings.HasPlacedWindow ? Math.Clamp(_settings.TopRatio, 0, 1) : 0.36;
        var top = area.Top + (area.Height - DockSize) * ratio;
        Left = _anchorRight ? area.Right - DockSize - EdgeInset : area.Left + EdgeInset;
        Top = Math.Clamp(top, area.Top + 8, area.Bottom - DockSize - 8);
    }

    private Rect GetWorkArea()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle != IntPtr.Zero ? Forms.Screen.FromHandle(handle) : Forms.Screen.PrimaryScreen;
        if (screen is null) return SystemParameters.WorkArea;
        var rect = screen.WorkingArea;
        return new Rect(
            rect.Left / dpi.DpiScaleX,
            rect.Top / dpi.DpiScaleY,
            rect.Width / dpi.DpiScaleX,
            rect.Height / dpi.DpiScaleY);
    }

    private void Dock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        App.Log("Dock mouse down");
        _dragPressed = true;
        _dragMoved = false;
        _dragStartCursor = Forms.Control.MousePosition;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        Dock.CaptureMouse();
        AnimateDockScale(0.96, 90);
        e.Handled = true;
    }

    private void Dock_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragPressed || e.LeftButton != MouseButtonState.Pressed) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = Forms.Control.MousePosition;
        var dx = (cursor.X - _dragStartCursor.X) / dpi.DpiScaleX;
        var dy = (cursor.Y - _dragStartCursor.Y) / dpi.DpiScaleY;
        if (!_dragMoved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;

        if (!_dragMoved)
        {
            _dragMoved = true;
            Dock.Cursor = Cursors.SizeAll;
            AnimateDockScale(1.02, 100);
        }

        var minLeft = SystemParameters.VirtualScreenLeft - DockSize + 24;
        var maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 24;
        var minTop = SystemParameters.VirtualScreenTop - 8;
        var maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 24;
        Left = Math.Clamp(_dragStartLeft + dx, minLeft, maxLeft);
        Top = Math.Clamp(_dragStartTop + dy, minTop, maxTop);
        e.Handled = true;
    }

    private void Dock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragPressed) return;
        App.Log($"Dock mouse up moved={_dragMoved}");
        _dragPressed = false;
        Dock.ReleaseMouseCapture();
        Dock.Cursor = Cursors.Hand;
        AnimateDockScale(1.035, 120);
        if (_dragMoved) SnapDockToEdge();
        else ShowPanel();
        _dragMoved = false;
        e.Handled = true;
    }

    private void Dock_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragPressed) return;
        _dragPressed = false;
        Dock.Cursor = Cursors.Hand;
        AnimateDockScale(1, 120);
    }

    private void SnapDockToEdge()
    {
        var area = GetWorkArea();
        var center = Left + DockSize / 2;
        _anchorRight = center >= area.Left + area.Width / 2;
        var targetLeft = _anchorRight ? area.Right - DockSize - EdgeInset : area.Left + EdgeInset;
        var targetTop = Math.Clamp(Top, area.Top + 8, area.Bottom - DockSize - 8);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var leftAnimation = new DoubleAnimation(Left, targetLeft, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        leftAnimation.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            Left = targetLeft;
        };
        BeginAnimation(LeftProperty, leftAnimation);

        var topAnimation = new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        topAnimation.Completed += (_, _) =>
        {
            BeginAnimation(TopProperty, null);
            Top = targetTop;
        };
        BeginAnimation(TopProperty, topAnimation);

        SavePlacement(_anchorRight ? "Right" : "Left", (targetTop - area.Top) / Math.Max(1, area.Height - DockSize));
    }

    private void AnimateDockScale(double target, double milliseconds = 150, bool fromCurrent = false)
    {
        if (fromCurrent) DockScale.ScaleX = DockScale.ScaleY = 0.92;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DockScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(DockScale.ScaleX, target, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
        DockScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(DockScale.ScaleY, target, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
    }

    private void WindowFadeIn()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_shotPath is not null || App.NoAutoHide) return;
        if (!_panelOpen || _modalDepth > 0) return;
        HidePanel();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.IsEditorOpen)
        {
            if (e.Key == Key.Escape)
            {
                _viewModel.CloseEditor();
                e.Handled = true;
            }

            return;
        }

        if (_viewModel.IsSettingsOpen)
        {
            if (e.Key == Key.Escape)
            {
                _viewModel.CloseOverlaysCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        var control = Keyboard.Modifiers == ModifierKeys.Control;
        if (control && e.Key == Key.K)
        {
            if (_viewModel.IsInsightsOpen) _viewModel.CloseOverlaysCommand.Execute(null);
            Panel.FocusSearch();
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.N)
        {
            _viewModel.OpenEditorForNew();
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.I)
        {
            _viewModel.ShowInsightsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsInsightsOpen) _viewModel.CloseOverlaysCommand.Execute(null);
            else if (_viewModel.IsSearching) _viewModel.ClearSearchCommand.Execute(null);
            else HidePanel();
            e.Handled = true;
            return;
        }

        if (!_panelOpen) return;
        if (_viewModel.IsInsightsOpen) return;

        if (e.Key is Key.Up or Key.Down && Keyboard.FocusedElement is not Button)
        {
            Panel.MoveSelection(e.Key == Key.Down ? 2 : -2);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.FocusedElement is not Button)
        {
            Panel.OpenSelected();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.CloseRequested -= HidePanel;
        _viewModel.ExitRequested -= ExitApplication;
        _hotkey.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        _viewModel.SavePlacementNow(_anchorRight ? "Right" : "Left", _settings.TopRatio);
    }

    private void CreateTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is not null) _trayIcon = new Drawing.Icon(resource.Stream, 32, 32);
        }
        catch
        {
            _trayIcon = null;
        }

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon ?? Drawing.SystemIcons.Application,
            Text = "Jump — 快捷启动",
            Visible = true
        };
        _tray.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowPanel);
            else if (args.Button == Forms.MouseButtons.Right) Dispatcher.Invoke(ShowTrayMenu);
        };
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var show = new MenuItem { Header = "打开快捷面板", InputGestureText = _viewModel.HotkeyDisplay };
        show.Click += (_, _) => ShowPanel();
        var overview = new MenuItem { Header = "使用概览" };
        overview.Click += (_, _) =>
        {
            _viewModel.OpenInsights();
            ShowPanel();
        };
        var settings = new MenuItem { Header = "设置…" };
        settings.Click += (_, _) =>
        {
            _viewModel.OpenSettings();
            ShowPanel();
        };
        var startup = new MenuItem { Header = "开机启动", IsCheckable = true, IsChecked = _viewModel.StartupEnabled };
        startup.Click += (_, _) => _viewModel.StartupEnabled = startup.IsChecked;
        var exit = new MenuItem { Header = "退出 Jump" };
        exit.Click += (_, _) => ExitApplication();

        menu.Items.Add(show);
        menu.Items.Add(overview);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(startup);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        menu.IsOpen = true;
    }

    private void ExitApplication()
    {
        _tray?.Dispose();
        _tray = null;
        System.Windows.Application.Current.Shutdown();
    }

    private void SavePlacement(string edge, double ratio)
    {
        _viewModel.SavePlacement(edge, ratio);
    }

    private async Task RunPreviewAsync()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        _viewModel.HotkeyAvailable = true;
        try
        {
            Forms.Cursor.Position = new Drawing.Point(1, 1);
        }
        catch
        {
            // Moving the pointer is only for deterministic screenshots.
        }

        if (_previewView == "dock")
        {
            var area = GetWorkArea();
            Width = DockSize;
            Height = DockSize;
            Left = area.Right - DockSize - 24;
            Top = area.Top + area.Height * 0.34;
            await Task.Delay(500);
            if (_shotPath is not null) RenderShot(_shotPath, 4);
            ShutdownPreview();
            return;
        }

        ShowPanel(center: true, animate: false);
        switch (_previewView)
        {
            case "insights":
                _viewModel.OpenInsights();
                break;
            case "insights-bottom":
                _viewModel.OpenInsights();
                _ = Dispatcher.BeginInvoke(() => Panel.ScrollInsightsTo(620), DispatcherPriority.ApplicationIdle);
                break;
            case "empty":
                _viewModel.SearchText = "zzz";
                break;
            case "settings":
                _viewModel.OpenSettings();
                break;
            case "editor":
                _viewModel.OpenEditorForNew();
                _viewModel.Editor.Fill("项目工作区", @"D:\code3\desktop-jump-list", "开发", ShortcutType.Folder);
                break;
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(900);
        if (_shotPath is not null)
        {
            RenderShot(_shotPath, 2);
            ShutdownPreview();
        }
    }

    private void ShutdownPreview()
    {
        Application.Current.Shutdown();
    }

    private void RenderShot(string path, int scale)
    {
        try
        {
            Root.UpdateLayout();
            var width = (int)Math.Ceiling(Root.ActualWidth * scale);
            var height = (int)Math.Ceiling(Root.ActualHeight * scale);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("窗口尚未完成布局。");

            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(Root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var stream = File.Create(fullPath);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(App.DataDirectory, "shot-error.txt"), ex.ToString());
            }
            catch
            {
                // Nothing else to do in render mode.
            }
        }
    }
}
