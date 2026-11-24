using System.Diagnostics;
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

/// <summary>
/// The round floating ball.
///
/// States
///   Resting  – fully visible, 14px from the screen edge.
///   Tucked   – slid into the edge after <see cref="AppSettings.PeekDelayMs"/> of no attention;
///              a 12px sliver stays visible at reduced opacity. Hovering slides it back out.
///   Dragging – follows the pointer freely; on release it glides to the nearest left/right edge.
///
/// The window itself never moves for tucking: only the ball's TranslateTransform does, and the
/// window bounds clip it. That keeps the motion on the GPU and perfectly smooth.
/// </summary>
public partial class DockWindow : Window
{
    private const double WindowSize = 72;
    private const double BallSize = 44;
    private const double Inset = (WindowSize - BallSize) / 2; // 14
    private const double Sliver = 12;
    private const double TuckOffset = BallSize + Inset - Sliver; // 46
    private const double DragThreshold = 4;

    private readonly AppSettings _settings;
    private readonly WindowMover _mover;
    private readonly DispatcherTimer _peekTimer;
    private readonly DispatcherTimer _fullscreenTimer;

    private bool _tucked;
    private bool _pressed;
    private bool _dragging;
    private bool _holdOpen;
    private bool _menuOpen;
    private bool _hiddenForFullscreen;
    private Native.POINT _pressCursor;
    private double _pressLeft, _pressTop;

    public DockWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _mover = new WindowMover(this);

        _peekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(600, settings.PeekDelayMs)) };
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); TryTuck(); };

        _fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();

        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, noActivate: true);
        Loaded += OnLoaded;

        Root.MouseEnter += OnEnter;
        Root.MouseLeave += OnLeave;
        Root.PreviewMouseLeftButtonDown += OnDown;
        Root.PreviewMouseMove += OnMove;
        Root.PreviewMouseLeftButtonUp += OnUp;
        Root.LostMouseCapture += (_, _) => { if (_pressed) EndPress(cancelled: true); };
        Root.MouseRightButtonUp += OnRightClick;

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PlaceAtEdge);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(PlaceAtEdge);
        };
    }

    private static Rect WorkArea => SystemParameters.WorkArea;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceAtEdge();
        // Arrive gently instead of popping in.
        Root.Opacity = 0;
        Root.To(OpacityProperty, 1, 260);
        BallScale.ScaleX = BallScale.ScaleY = 0.6;
        BallScale.To(ScaleTransform.ScaleXProperty, 1, 320, Motion.OutStrong);
        BallScale.To(ScaleTransform.ScaleYProperty, 1, 320, Motion.OutStrong);
        _peekTimer.Start();
        _fullscreenTimer.Start();
    }

    // ───────────────────────── Placement ─────────────────────────

    private double EdgeLeft(DockEdge edge) => edge == DockEdge.Right ? WorkArea.Right - WindowSize : WorkArea.Left;

    private double ClampTop(double top) => Math.Clamp(top, WorkArea.Top, WorkArea.Bottom - WindowSize);

    private void PlaceAtEdge()
    {
        if (_dragging) return;
        _mover.Stop();
        Left = EdgeLeft(_settings.Edge);
        Top = ClampTop(WorkArea.Top + _settings.EdgeRatio * (WorkArea.Height - WindowSize));
        ApplyEdgeLayout();
        if (_tucked) BallShift.Set(TranslateTransform.XProperty, TuckDirection * TuckOffset);
    }

    private double TuckDirection => _settings.Edge == DockEdge.Right ? 1 : -1;

    private void ApplyEdgeLayout()
    {
        HitStrip.HorizontalAlignment = _settings.Edge == DockEdge.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    // ───────────────────────── Tuck / peek ─────────────────────────

    private void RestartPeekTimer()
    {
        _peekTimer.Stop();
        if (!_holdOpen && !_menuOpen && !_dragging) _peekTimer.Start();
    }

    private void TryTuck()
    {
        if (_tucked || _holdOpen || _menuOpen || _dragging || _pressed || Root.IsMouseOver) return;
        _tucked = true;
        BallShift.To(TranslateTransform.XProperty, TuckDirection * TuckOffset, 360, Motion.InOut);
        Ball.To(OpacityProperty, 0.72, 360, Motion.InOut);
        ScaleBall(1, 200);
    }

    private void Untuck(double ms = 240)
    {
        if (!_tucked) return;
        _tucked = false;
        BallShift.To(TranslateTransform.XProperty, 0, ms, Motion.OutStrong);
        Ball.To(OpacityProperty, 1, ms * 0.8);
    }

    /// <summary>While the launcher is open the ball stays out and awake.</summary>
    public void HoldOpen(bool hold)
    {
        _holdOpen = hold;
        if (hold)
        {
            _peekTimer.Stop();
            Untuck();
        }
        else
        {
            if (!Root.IsMouseOver) ScaleBall(1, 160);
            RestartPeekTimer();
        }
    }

    private void ScaleBall(double to, double ms)
    {
        BallScale.To(ScaleTransform.ScaleXProperty, to, ms);
        BallScale.To(ScaleTransform.ScaleYProperty, to, ms);
    }

    // ───────────────────────── Hover ─────────────────────────

    private void OnEnter(object sender, MouseEventArgs e)
    {
        _peekTimer.Stop();
        if (_tucked) Untuck();
        else if (!_pressed) ScaleBall(1.07, 160);
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        if (_pressed) return;
        ScaleBall(1, 180);
        RestartPeekTimer();
    }

    // ───────────────────────── Press / drag / click ─────────────────────────

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Native.GetCursorPos(out _pressCursor);
        _pressLeft = Left;
        _pressTop = Top;
        _pressed = true;
        _dragging = false;
        _peekTimer.Stop();
        Root.CaptureMouse();
        ScaleBall(0.93, 110);
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        Native.GetCursorPos(out var now);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var dx = (now.X - _pressCursor.X) / scale;
        var dy = (now.Y - _pressCursor.Y) / scale;

        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;
            _dragging = true;
            _mover.Stop();
            Untuck(140);
            ScaleBall(1.0, 140);
            Ball.To(OpacityProperty, 0.92, 140);
        }

        Left = _pressLeft + dx;
        Top = _pressTop + dy;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        e.Handled = true;
        EndPress(cancelled: false);
    }

    private void EndPress(bool cancelled)
    {
        var wasDragging = _dragging;
        _pressed = false;
        _dragging = false;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();

        if (wasDragging)
        {
            Ball.To(OpacityProperty, 1, 160);
            SnapToEdge();
            return;
        }

        ScaleBall(Root.IsMouseOver ? 1.07 : 1, 180);
        if (!cancelled) App.Current.ToggleLauncher();
        else RestartPeekTimer();
    }

    private void SnapToEdge()
    {
        var center = Left + WindowSize / 2;
        _settings.Edge = center < WorkArea.Left + WorkArea.Width / 2 ? DockEdge.Left : DockEdge.Right;
        var targetTop = ClampTop(Top);
        var range = WorkArea.Height - WindowSize;
        _settings.EdgeRatio = range > 0 ? (targetTop - WorkArea.Top) / range : 0.5;
        ApplyEdgeLayout();
        App.Current.SaveSettings();

        _mover.MoveTo(EdgeLeft(_settings.Edge), targetTop, 260, () =>
        {
            ScaleBall(Root.IsMouseOver ? 1.07 : 1, 160);
            RestartPeekTimer();
        });
    }

    // ───────────────────────── Context menu ─────────────────────────

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var autostart = new MenuItem { Header = "开机自动启动", IsCheckable = true, IsChecked = StartupService.IsEnabled };
        autostart.Click += (_, _) => { try { StartupService.Set(autostart.IsChecked); } catch { } };

        var data = new MenuItem { Header = "打开数据目录" };
        data.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.Current.Storage.Directory}\"") { UseShellExecute = true });

        var quit = new MenuItem { Header = "退出 Jump" };
        quit.Click += (_, _) => FadeOutAndQuit();

        var open = new MenuItem { Header = "打开面板", InputGestureText = _settings.Hotkey };
        open.Click += (_, _) => App.Current.ShowLauncher();

        // 外观主题（扁平展示，无需悬停二级菜单）
        var themeSys = new MenuItem { Header = "外观：跟随系统", IsCheckable = true };
        var themeLight = new MenuItem { Header = "外观：浅色", IsCheckable = true };
        var themeDark = new MenuItem { Header = "外观：深色", IsCheckable = true };
        themeSys.IsChecked = _settings.Theme == AppTheme.System;
        themeLight.IsChecked = _settings.Theme == AppTheme.Light;
        themeDark.IsChecked = _settings.Theme == AppTheme.Dark;
        themeSys.Click += (_, _) => { App.Current.SetTheme(AppTheme.System); themeSys.IsChecked = true; themeLight.IsChecked = false; themeDark.IsChecked = false; };
        themeLight.Click += (_, _) => { App.Current.SetTheme(AppTheme.Light); themeSys.IsChecked = false; themeLight.IsChecked = true; themeDark.IsChecked = false; };
        themeDark.Click += (_, _) => { App.Current.SetTheme(AppTheme.Dark); themeSys.IsChecked = false; themeLight.IsChecked = false; themeDark.IsChecked = true; };

        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(themeSys);
        menu.Items.Add(themeLight);
        menu.Items.Add(themeDark);
        menu.Items.Add(new Separator());
        menu.Items.Add(autostart);
        menu.Items.Add(data);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);

        _menuOpen = true;
        _peekTimer.Stop();
        menu.Closed += (_, _) => { _menuOpen = false; if (!Root.IsMouseOver) ScaleBall(1, 160); RestartPeekTimer(); };
        Root.ContextMenu = menu;
        menu.PlacementTarget = Root;
        menu.IsOpen = true;
    }

    private void FadeOutAndQuit()
    {
        _peekTimer.Stop();
        _fullscreenTimer.Stop();
        ScaleBall(0.6, 180);
        Root.To(OpacityProperty, 0, 180, Motion.In, completed: () => App.Current.Quit());
    }

    // ───────────────────────── Full-screen courtesy ─────────────────────────

    /// <summary>Fade away while a full-screen app, game or presentation is in front.</summary>
    private void CheckFullscreen()
    {
        if (_holdOpen) return;
        bool busy = Native.SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
        if (busy == _hiddenForFullscreen) return;
        _hiddenForFullscreen = busy;
        Root.IsHitTestVisible = !busy;
        Root.To(OpacityProperty, busy ? 0 : 1, busy ? 200 : 260);
    }
}
