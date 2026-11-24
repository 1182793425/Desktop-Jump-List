using System.Diagnostics;
using System.Runtime.InteropServices;
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
///   Resting  – fully visible, insetted from whichever screen edge it is docked to.
///   Tucked   – slid into the edge after <see cref="AppSettings.PeekDelayMs"/> of no attention;
///              a small sliver stays visible at reduced opacity. Hovering slides it back out.
///   Dragging – follows the pointer freely; on release it snaps to the nearest edge only when
///              dropped close to it (within <see cref="AppSettings.SnapDistance"/>), otherwise it
///              stays wherever you left it (free placement).
///
/// Multi-monitor: the work area is resolved from the monitor currently under the ball.
/// The window itself never moves for tucking: only the ball's TranslateTransform does.
/// </summary>
public partial class DockWindow : Window
{
    private const double WindowSize = 72;
    private const double BallSize = 44;
    private const double Inset = (WindowSize - BallSize) / 2; // 14
    private const double Sliver = 12;
    private const double TuckOffset = BallSize + Inset - Sliver; // 46
    private const double DragThreshold = 4;
    private const uint MonitorDefaultToNearest = 2;

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

        SourceInitialized += (_, _) => Native.MakeNoActivate(new WindowInteropHelper(this).Handle);
        Loaded += OnLoaded;

        Root.MouseEnter += OnEnter;
        Root.MouseLeave += OnLeave;
        Root.PreviewMouseLeftButtonDown += OnDown;
        Root.PreviewMouseMove += OnMove;
        Root.PreviewMouseLeftButtonUp += OnUp;
        Root.LostMouseCapture += (_, _) => { if (_pressed) EndPress(cancelled: true); };
        Root.MouseRightButtonUp += OnRightClick;

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PlaceInitial);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(PlaceInitial);
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceInitial();
        Root.Opacity = 0;
        Root.To(OpacityProperty, 1, 260);
        BallScale.ScaleX = BallScale.ScaleY = 0.6;
        BallScale.To(ScaleTransform.ScaleXProperty, 1, 320, Motion.OutStrong);
        BallScale.To(ScaleTransform.ScaleYProperty, 1, 320, Motion.OutStrong);
        _peekTimer.Start();
        _fullscreenTimer.Start();
    }

    // ───────────────────────── Screen geometry ─────────────────────────

    /// <summary>Work area (DIPs) of the monitor containing the given DIP point.</summary>
    private Rect WorkAreaFor(double left, double top)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var pt = new Native.POINT
            {
                X = (int)((left + WindowSize / 2) * dpi.DpiScaleX),
                Y = (int)((top + WindowSize / 2) * dpi.DpiScaleY)
            };
            var h = Native.MonitorFromPoint(pt, MonitorDefaultToNearest);
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (h != IntPtr.Zero && Native.GetMonitorInfo(h, ref info))
            {
                var sx = dpi.DpiScaleX;
                var sy = dpi.DpiScaleY;
                return new Rect(
                    info.rcWork.Left / sx, info.rcWork.Top / sy,
                    (info.rcWork.Right - info.rcWork.Left) / sx,
                    (info.rcWork.Bottom - info.rcWork.Top) / sy);
            }
        }
        catch { }
        return SystemParameters.WorkArea;
    }

    private Rect CurrentArea => WorkAreaFor(Left, Top);

    // ───────────────────────── Placement ─────────────────────────

    private void PlaceInitial()
    {
        if (_dragging) return;
        _mover.Stop();

        if (_settings.Edge == DockEdge.None && _settings.FreeLeft >= 0 && _settings.FreeTop >= 0)
        {
            var area = WorkAreaFor(_settings.FreeLeft, _settings.FreeTop);
            Left = Clamp(_settings.FreeLeft, area.Left, area.Right - WindowSize);
            Top = Clamp(_settings.FreeTop, area.Top, area.Bottom - WindowSize);
        }
        else
        {
            var area = WorkAreaFor(Left, Top);
            var (l, t) = EdgePosition(area, _settings.Edge, _settings.EdgeRatio);
            Left = l;
            Top = t;
        }

        ApplyEdgeLayout();
        if (_tucked) SetTuckInstant(true);
    }

    private static double Clamp(double v, double lo, double hi) => hi < lo ? lo : Math.Clamp(v, lo, hi);

    /// <summary>Ball position for a docked edge and a 0..1 ratio along that edge.</summary>
    private static (double left, double top) EdgePosition(Rect area, DockEdge edge, double ratio)
    {
        var r = Math.Clamp(ratio, 0, 1);
        var vr = area.Top + r * Math.Max(0, area.Height - WindowSize);
        var hr = area.Left + r * Math.Max(0, area.Width - WindowSize);
        return edge switch
        {
            DockEdge.Left => (area.Left, vr),
            DockEdge.Right => (area.Right - WindowSize, vr),
            DockEdge.Top => (hr, area.Top),
            DockEdge.Bottom => (hr, area.Bottom - WindowSize),
            _ => (area.Left, area.Top)
        };
    }

    private static double VerticalRatio(Rect area, double top)
    {
        var range = area.Height - WindowSize;
        return range <= 0 ? 0.5 : Math.Clamp((top - area.Top) / range, 0, 1);
    }

    private static double HorizontalRatio(Rect area, double left)
    {
        var range = area.Width - WindowSize;
        return range <= 0 ? 0.5 : Math.Clamp((left - area.Left) / range, 0, 1);
    }

    private void ApplyEdgeLayout()
    {
        // The peek hit-strip sits on the edge the ball is tucked into.
        switch (_settings.Edge)
        {
            case DockEdge.Left:
                HitStrip.Width = 14; HitStrip.Height = 52;
                HitStrip.HorizontalAlignment = HorizontalAlignment.Left;
                HitStrip.VerticalAlignment = VerticalAlignment.Center;
                break;
            case DockEdge.Top:
                HitStrip.Width = 52; HitStrip.Height = 14;
                HitStrip.HorizontalAlignment = HorizontalAlignment.Center;
                HitStrip.VerticalAlignment = VerticalAlignment.Top;
                break;
            case DockEdge.Bottom:
                HitStrip.Width = 52; HitStrip.Height = 14;
                HitStrip.HorizontalAlignment = HorizontalAlignment.Center;
                HitStrip.VerticalAlignment = VerticalAlignment.Bottom;
                break;
            case DockEdge.None:
                HitStrip.Width = 0; HitStrip.Height = 0; // no tuck when free
                break;
            default:
                HitStrip.Width = 14; HitStrip.Height = 52;
                HitStrip.HorizontalAlignment = HorizontalAlignment.Right;
                HitStrip.VerticalAlignment = VerticalAlignment.Center;
                break;
        }
    }

    // ───────────────────────── Tuck / peek ─────────────────────────

    private void SetTuckInstant(bool tucked)
    {
        var (x, y) = TuckVector();
        BallShift.Set(TranslateTransform.XProperty, tucked ? x : 0);
        BallShift.Set(TranslateTransform.YProperty, tucked ? y : 0);
    }

    private (double x, double y) TuckVector() => _settings.Edge switch
    {
        DockEdge.Right => (TuckOffset, 0),
        DockEdge.Left => (-TuckOffset, 0),
        DockEdge.Top => (0, -TuckOffset),
        DockEdge.Bottom => (0, TuckOffset),
        _ => (0, 0)
    };

    private void RestartPeekTimer()
    {
        _peekTimer.Stop();
        if (!_holdOpen && !_menuOpen && !_dragging && _settings.Edge != DockEdge.None)
            _peekTimer.Start();
    }

    private void TryTuck()
    {
        if (_tucked || _holdOpen || _menuOpen || _dragging || _pressed || Root.IsMouseOver) return;
        if (_settings.Edge == DockEdge.None) return;
        _tucked = true;
        var (x, y) = TuckVector();
        BallShift.To(TranslateTransform.XProperty, x, 360, Motion.InOut);
        BallShift.To(TranslateTransform.YProperty, y, 360, Motion.InOut);
        Ball.To(OpacityProperty, 0.72, 360, Motion.InOut);
        ScaleBall(1, 200);
    }

    private void Untuck(double ms = 240)
    {
        if (!_tucked) return;
        _tucked = false;
        BallShift.To(TranslateTransform.XProperty, 0, ms, Motion.OutStrong);
        BallShift.To(TranslateTransform.YProperty, 0, ms, Motion.OutStrong);
        Ball.To(OpacityProperty, 1, ms * 0.8);
    }

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
        var dpi = VisualTreeHelper.GetDpi(this);
        var dx = (now.X - _pressCursor.X) / dpi.DpiScaleX;
        var dy = (now.Y - _pressCursor.Y) / dpi.DpiScaleY;

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
            SnapOnRelease();
            return;
        }

        ScaleBall(Root.IsMouseOver ? 1.07 : 1, 180);
        if (!cancelled) App.Current.ToggleLauncher();
        else RestartPeekTimer();
    }

    /// <summary>
    /// On release, snap to the nearest edge only if the ball is within SnapDistance of it
    /// (a tight trigger, unlike the old half-screen rule). Otherwise keep the drop position.
    /// </summary>
    private void SnapOnRelease()
    {
        var area = CurrentArea;
        var dist = Math.Max(8, _settings.SnapDistance);

        var candidates = new (DockEdge edge, double d)[]
        {
            (DockEdge.Left, Left - area.Left),
            (DockEdge.Right, area.Right - (Left + WindowSize)),
            (DockEdge.Top, Top - area.Top),
            (DockEdge.Bottom, area.Bottom - (Top + WindowSize))
        };

        var within = candidates.Where(c => c.d <= dist).OrderBy(c => c.d).ToList();
        if (within.Count == 0)
        {
            // Free placement: remember the absolute position and stay put.
            _settings.Edge = DockEdge.None;
            _settings.FreeLeft = Clamp(Left, area.Left, area.Right - WindowSize);
            _settings.FreeTop = Clamp(Top, area.Top, area.Bottom - WindowSize);
            ApplyEdgeLayout();
            App.Current.SaveSettings();
            _mover.MoveTo(_settings.FreeLeft, _settings.FreeTop, 220, Done);
            return;
        }

        var edge = within[0].edge;
        _settings.Edge = edge;
        _settings.EdgeRatio = edge is DockEdge.Left or DockEdge.Right
            ? VerticalRatio(area, Top)
            : HorizontalRatio(area, Left);
        var (l, t) = EdgePosition(area, edge, _settings.EdgeRatio);
        ApplyEdgeLayout();
        App.Current.SaveSettings();
        _mover.MoveTo(l, t, 240, Done);

        void Done()
        {
            ScaleBall(Root.IsMouseOver ? 1.07 : 1, 160);
            RestartPeekTimer();
        }
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

        // 外观主题（扁平展示）
        var themeSys = new MenuItem { Header = "外观：跟随系统", IsCheckable = true };
        var themeLight = new MenuItem { Header = "外观：浅色", IsCheckable = true };
        var themeDark = new MenuItem { Header = "外观：深色", IsCheckable = true };
        themeSys.IsChecked = _settings.Theme == AppTheme.System;
        themeLight.IsChecked = _settings.Theme == AppTheme.Light;
        themeDark.IsChecked = _settings.Theme == AppTheme.Dark;
        themeSys.Click += (_, _) => { App.Current.SetTheme(AppTheme.System); themeSys.IsChecked = true; themeLight.IsChecked = false; themeDark.IsChecked = false; };
        themeLight.Click += (_, _) => { App.Current.SetTheme(AppTheme.Light); themeSys.IsChecked = false; themeLight.IsChecked = true; themeDark.IsChecked = false; };
        themeDark.Click += (_, _) => { App.Current.SetTheme(AppTheme.Dark); themeSys.IsChecked = false; themeLight.IsChecked = false; themeDark.IsChecked = true; };

        // 布局：每行数量 / 最大行数
        var layout = new MenuItem { Header = "布局" };
        var colMenu = new MenuItem { Header = "每行数量" };
        foreach (var n in new[] { 3, 4, 5, 6, 7 })
        {
            var c = new MenuItem { Header = n.ToString(), IsCheckable = true, IsChecked = _settings.Columns == n };
            var val = n;
            c.Click += (_, _) => { _settings.Columns = val; App.Current.SaveSettings(); App.Current.Launcher.ApplyLayout(); };
            colMenu.Items.Add(c);
        }
        var rowMenu = new MenuItem { Header = "最大行数" };
        foreach (var n in new[] { 2, 3, 4, 5, 6 })
        {
            var c = new MenuItem { Header = n.ToString(), IsCheckable = true, IsChecked = _settings.MaxRows == n };
            var val = n;
            c.Click += (_, _) => { _settings.MaxRows = val; App.Current.SaveSettings(); App.Current.Launcher.ApplyLayout(); };
            rowMenu.Items.Add(c);
        }
        layout.Items.Add(colMenu);
        layout.Items.Add(rowMenu);

        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(themeSys);
        menu.Items.Add(themeLight);
        menu.Items.Add(themeDark);
        menu.Items.Add(layout);
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
