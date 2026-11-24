using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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

/// <summary>
/// The floating circle.
///
/// States
///   Resting  – fully visible, insetted from whichever screen edge it is docked to.
///   Tucked   – after <see cref="AppSettings.PeekDelayMs"/> of no attention the circle morphs
///              into a slim capsule and slides until it is flush with the screen edge, leaving a
///              visible bar. Hovering slides it back out into a circle.
///   Dragging – follows the pointer freely; on release it snaps to the nearest edge only when
///              dropped close to it (within <see cref="AppSettings.SnapDistance"/>), otherwise it
///              stays wherever you left it (free placement).
///
/// Size, fill colour and visibility come from settings. The window never moves for tucking:
/// the circle's transform and the shape's width/height animate instead.
/// </summary>
public partial class DockWindow : Window
{
    private const double ShadowMargin = 16;
    private const double BarWidth = 6;
    private const double DragThreshold = 4;

    private readonly AppSettings _settings;
    private readonly WindowMover _mover;
    private readonly DispatcherTimer _peekTimer;
    private readonly DispatcherTimer _fullscreenTimer;

    private double _ballSize;
    private double _windowSize;
    private RectangleGeometry _ballGeometry = null!;

    private bool _tucked;
    private bool _pressed;
    private bool _dragging;
    private bool _holdOpen;
    private bool _menuOpen;
    private bool _hiddenForFullscreen;
    private Native.POINT _pressCursor;
    private double _pressLeft, _pressTop;

    /// <summary>Developer preview mode: no idle timers, so a capture can inspect a fixed state.</summary>
    public bool PreviewMode { get; set; }

    public DockWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ApplyVisualSettings();
        SetTuckInstant(false);

        _mover = new WindowMover(this);

        _peekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, settings.PeekDelayMs)) };
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

        SystemEvents.DisplaySettingsChanged += (_, _) =>
        {
            try { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => PlaceInitial()); }
            catch { }
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SystemParameters.WorkArea)) return;
            try { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => PlaceInitial()); }
            catch { }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hiddenForFullscreen = false;
        Root.IsHitTestVisible = true;
        PlaceInitial(useCursor: true);
        if (PreviewMode)
        {
            Root.Set(OpacityProperty, 1);
            BallScale.Set(ScaleTransform.ScaleXProperty, 1);
            BallScale.Set(ScaleTransform.ScaleYProperty, 1);
            return;
        }

        Root.Opacity = 0;
        Root.To(OpacityProperty, 1, 260);
        BallScale.ScaleX = BallScale.ScaleY = 0.6;
        BallScale.To(ScaleTransform.ScaleXProperty, 1, 320, Motion.OutStrong);
        BallScale.To(ScaleTransform.ScaleYProperty, 1, 320, Motion.OutStrong);
        _peekTimer.Start();
        _fullscreenTimer.Start();
    }

    // ───────────────────────── Appearance ─────────────────────────

    private double BarLength => Math.Max(28, _ballSize * 0.82);
    private double TuckOffset => (_windowSize - BarWidth) / 2;

    private void ApplyVisualSettings()
    {
        _ballSize = Math.Max(0, _settings.DockSize);
        _windowSize = _ballSize + ShadowMargin * 2;

        Width = _windowSize;
        Height = _windowSize;
        Ball.Width = _ballSize;
        Ball.Height = _ballSize;
        BallShape.Width = _ballSize;
        BallShape.Height = _ballSize;

        var restRect = new Rect(0, 0, _ballSize, _ballSize);
        _ballGeometry = new RectangleGeometry(
            _tucked ? BarRect() : restRect,
            _tucked ? BarWidth / 2 : _ballSize / 2,
            _tucked ? BarWidth / 2 : _ballSize / 2);
        BallShape.Data = _ballGeometry;

        var color = ParseColor(_settings.DockColor);
        BallShape.Fill = new SolidColorBrush(color);
        BallShape.Stroke = new SolidColorBrush(ContrastBorder(color));
    }

    private Rect BarRect()
    {
        var (width, height) = BarSize(true);
        return new Rect((_ballSize - width) / 2, (_ballSize - height) / 2, width, height);
    }

    private static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return Colors.White; }
    }

    private static Color ContrastBorder(Color color)
    {
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
        return luminance > 0.6 ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x44, 255, 255, 255);
    }

    private (double width, double height) BarSize(bool tucked)
    {
        if (!tucked) return (_ballSize, _ballSize);
        return _settings.Edge is DockEdge.Left or DockEdge.Right
            ? (BarWidth, BarLength)
            : (BarLength, BarWidth);
    }

    /// <summary>Re-applies settings changed from the launcher, tray menu or settings page.</summary>
    public void ApplySettings()
    {
        var previousWindowSize = _windowSize;
        _peekTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _settings.PeekDelayMs));
        ApplyVisualSettings();

        if (!_settings.ShowDock)
        {
            _tucked = false;
            _holdOpen = false;
            SetTuckInstant(false);
            _peekTimer.Stop();
            _fullscreenTimer.Stop();
            Hide();
            return;
        }

        if (!IsVisible)
        {
            _tucked = false;
            SetTuckInstant(false);
            Show();
            PlaceInitial();
            _fullscreenTimer.Start();
            RestartPeekTimer();
            return;
        }

        if (Math.Abs(previousWindowSize - _windowSize) > 0.01)
            PlaceInitial();
        else
        {
            ApplyEdgeLayout();
            SetTuckInstant(_tucked);
        }

        if (!_fullscreenTimer.IsEnabled) _fullscreenTimer.Start();
        RestartPeekTimer();
    }

    // ───────────────────────── Screen geometry ─────────────────────────

    private Rect CurrentArea => ScreenGeometry.WorkAreaAtPoint(Left + _windowSize / 2, Top + _windowSize / 2, this);

    // ───────────────────────── Placement ─────────────────────────

    private void PlaceInitial(bool useCursor = false)
    {
        if (_dragging) return;
        _mover.Stop();

        if (_settings.Edge == DockEdge.None && _settings.FreeLeft >= 0 && _settings.FreeTop >= 0)
        {
            var area = ScreenGeometry.WorkAreaAtPoint(_settings.FreeLeft, _settings.FreeTop, this);
            Left = Clamp(_settings.FreeLeft, area.Left, area.Right - _windowSize);
            Top = Clamp(_settings.FreeTop, area.Top, area.Bottom - _windowSize);
        }
        else
        {
            var area = useCursor
                ? ScreenGeometry.WorkAreaAtCursor(this)
                : ScreenGeometry.WorkAreaAtPoint(Left, Top, this);
            var (l, t) = EdgePosition(area, _settings.Edge, _settings.EdgeRatio);
            Left = l;
            Top = t;
        }

        ApplyEdgeLayout();
        if (_tucked) SetTuckInstant(true);
    }

    private static double Clamp(double v, double lo, double hi) => hi < lo ? lo : Math.Clamp(v, lo, hi);

    /// <summary>Circle position for a docked edge and a 0..1 ratio along that edge.</summary>
    private (double left, double top) EdgePosition(Rect area, DockEdge edge, double ratio)
    {
        var r = Math.Clamp(ratio, 0, 1);
        var vr = area.Top + r * Math.Max(0, area.Height - _windowSize);
        var hr = area.Left + r * Math.Max(0, area.Width - _windowSize);
        return edge switch
        {
            DockEdge.Left => (area.Left, vr),
            DockEdge.Right => (area.Right - _windowSize, vr),
            DockEdge.Top => (hr, area.Top),
            DockEdge.Bottom => (hr, area.Bottom - _windowSize),
            _ => (area.Left, area.Top)
        };
    }

    private double VerticalRatio(Rect area, double top)
    {
        var range = area.Height - _windowSize;
        return range <= 0 ? 0.5 : Math.Clamp((top - area.Top) / range, 0, 1);
    }

    private double HorizontalRatio(Rect area, double left)
    {
        var range = area.Width - _windowSize;
        return range <= 0 ? 0.5 : Math.Clamp((left - area.Left) / range, 0, 1);
    }

    private void ApplyEdgeLayout()
    {
        // The peek hit-strip sits on the edge the circle is docked to.
        switch (_settings.Edge)
        {
            case DockEdge.Left:
                HitStrip.Width = 18; HitStrip.Height = 64;
                HitStrip.HorizontalAlignment = HorizontalAlignment.Left;
                HitStrip.VerticalAlignment = VerticalAlignment.Center;
                break;
            case DockEdge.Top:
                HitStrip.Width = 64; HitStrip.Height = 18;
                HitStrip.HorizontalAlignment = HorizontalAlignment.Center;
                HitStrip.VerticalAlignment = VerticalAlignment.Top;
                break;
            case DockEdge.Bottom:
                HitStrip.Width = 64; HitStrip.Height = 18;
                HitStrip.HorizontalAlignment = HorizontalAlignment.Center;
                HitStrip.VerticalAlignment = VerticalAlignment.Bottom;
                break;
            case DockEdge.None:
                HitStrip.Width = 0; HitStrip.Height = 0; // no tuck when free
                break;
            default:
                HitStrip.Width = 18; HitStrip.Height = 64;
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

        var rect = tucked ? BarRect() : new Rect(0, 0, _ballSize, _ballSize);
        SetGeometryInstant(rect, tucked ? BarWidth / 2 : _ballSize / 2);
    }

    private void SetGeometryInstant(Rect rect, double radius)
    {
        _ballGeometry.BeginAnimation(RectangleGeometry.RectProperty, null);
        _ballGeometry.BeginAnimation(RectangleGeometry.RadiusXProperty, null);
        _ballGeometry.BeginAnimation(RectangleGeometry.RadiusYProperty, null);
        _ballGeometry.Rect = rect;
        _ballGeometry.RadiusX = radius;
        _ballGeometry.RadiusY = radius;
    }

    private void AnimateGeometry(Rect rect, double radius, double ms, IEasingFunction? ease = null, double delayMs = 0)
    {
        var easing = ease ?? Motion.InOut;
        var rectAnimation = new RectAnimation(rect, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = easing,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            FillBehavior = FillBehavior.HoldEnd
        };
        _ballGeometry.BeginAnimation(RectangleGeometry.RectProperty, rectAnimation, HandoffBehavior.SnapshotAndReplace);
        _ballGeometry.To(RectangleGeometry.RadiusXProperty, radius, ms, easing, delayMs);
        _ballGeometry.To(RectangleGeometry.RadiusYProperty, radius, ms, easing, delayMs);
    }

    private (double x, double y) TuckVector() => _settings.Edge switch
    {
        DockEdge.Right => (TuckOffset, 0),
        DockEdge.Left => (-TuckOffset, 0),
        DockEdge.Top => (0, -TuckOffset),
        DockEdge.Bottom => (0, TuckOffset),
        _ => (0, 0)
    };

    /// <summary>Developer preview: freeze the tucked bar for a screenshot.</summary>
    public void PreviewSetTucked(bool tucked)
    {
        _tucked = tucked;
        SetTuckInstant(tucked);
    }

    private void RestartPeekTimer()
    {
        _peekTimer.Stop();
        if (PreviewMode || !_settings.ShowDock || !IsVisible) return;
        if (!_holdOpen && !_menuOpen && !_dragging && _settings.Edge != DockEdge.None)
            _peekTimer.Start();
    }

    private void TryTuck()
    {
        if (_tucked || _holdOpen || _menuOpen || _dragging || _pressed || Root.IsMouseOver) return;
        if (!_settings.ShowDock || !IsVisible) return;
        if (_settings.Edge == DockEdge.None) return;
        _tucked = true;

        var (x, y) = TuckVector();
        var horizontal = _settings.Edge is DockEdge.Left or DockEdge.Right;
        const double pre = 70;
        const double total = 320;
        var main = total - pre;

        // Anticipation: squash against the edge, then flow into the slim bar with a
        // small settle instead of the old straight slide + shrink.
        BallShift.To(TranslateTransform.XProperty, x * 0.10, pre, Motion.Out);
        BallShift.To(TranslateTransform.YProperty, y * 0.10, pre, Motion.Out);
        SquashBall(horizontal ? 0.90 : 1.06, horizontal ? 1.06 : 0.90, pre, Motion.Out);

        BallShift.To(TranslateTransform.XProperty, x, main, Motion.Pop, pre);
        BallShift.To(TranslateTransform.YProperty, y, main, Motion.Pop, pre);
        SquashBall(1, 1, main, Motion.Pop, pre);
        AnimateGeometry(BarRect(), BarWidth / 2, main, Motion.Pop, pre);
    }

    private void Untuck(double ms = 260)
    {
        if (!_tucked) return;
        _tucked = false;

        var (x, y) = TuckVector();
        var horizontal = _settings.Edge is DockEdge.Left or DockEdge.Right;
        var pre = Math.Min(55, ms * 0.25);
        var main = ms - pre;

        // Wind up into the edge, then pop the circle out with a little overshoot.
        BallShift.To(TranslateTransform.XProperty, x * 1.06, pre, Motion.In);
        BallShift.To(TranslateTransform.YProperty, y * 1.06, pre, Motion.In);
        SquashBall(horizontal ? 0.92 : 1.08, horizontal ? 1.08 : 0.92, pre, Motion.In);

        BallShift.To(TranslateTransform.XProperty, 0, main, Motion.Pop, pre);
        BallShift.To(TranslateTransform.YProperty, 0, main, Motion.Pop, pre);
        AnimateGeometry(new Rect(0, 0, _ballSize, _ballSize), _ballSize / 2, main, Motion.Pop, pre);
        var hoverScale = Root.IsMouseOver ? 1.07 : 1;
        SquashBall(hoverScale, hoverScale, main, Motion.Pop, pre);
    }

    public void HoldOpen(bool hold)
    {
        if (!_settings.ShowDock || !IsVisible) return;
        _holdOpen = hold;
        if (hold)
        {
            _peekTimer.Stop();
            if (_tucked) Untuck();
        }
        else
        {
            if (!Root.IsMouseOver) ScaleBall(1, 160);
            RestartPeekTimer();
        }
    }

    private void ScaleBall(double to, double ms, IEasingFunction? ease = null)
    {
        BallScale.To(ScaleTransform.ScaleXProperty, to, ms, ease);
        BallScale.To(ScaleTransform.ScaleYProperty, to, ms, ease);
    }

    private void SquashBall(double scaleX, double scaleY, double ms, IEasingFunction? ease = null, double delayMs = 0)
    {
        BallScale.To(ScaleTransform.ScaleXProperty, scaleX, ms, ease, delayMs);
        BallScale.To(ScaleTransform.ScaleYProperty, scaleY, ms, ease, delayMs);
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
    /// On release, snap to the nearest edge only if the circle is within SnapDistance of it
    /// (a tight trigger). Otherwise keep the drop position.
    /// </summary>
    private void SnapOnRelease()
    {
        var area = CurrentArea;
        var dist = Math.Max(0, _settings.SnapDistance);

        var candidates = new (DockEdge edge, double d)[]
        {
            (DockEdge.Left, Left - area.Left),
            (DockEdge.Right, area.Right - (Left + _windowSize)),
            (DockEdge.Top, Top - area.Top),
            (DockEdge.Bottom, area.Bottom - (Top + _windowSize))
        };

        var within = candidates.Where(c => c.d <= dist).OrderBy(c => c.d).ToList();
        if (within.Count == 0)
        {
            // Free placement: remember the absolute position and stay put.
            _settings.Edge = DockEdge.None;
            _settings.FreeLeft = Clamp(Left, area.Left, area.Right - _windowSize);
            _settings.FreeTop = Clamp(Top, area.Top, area.Bottom - _windowSize);
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

        // Squash while the window glides to the edge, then spring back on arrival.
        var horizontal = edge is DockEdge.Left or DockEdge.Right;
        SquashBall(horizontal ? 0.95 : 1.05, horizontal ? 1.05 : 0.95, 130, Motion.Out);
        _mover.MoveTo(l, t, 240, Done);

        void Done()
        {
            ScaleBall(Root.IsMouseOver ? 1.07 : 1, 220, Motion.Pop);
            RestartPeekTimer();
        }
    }

    // ───────────────────────── Context menu ─────────────────────────

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var autostart = new MenuItem { Header = "开机自动启动", IsCheckable = true, IsChecked = StartupService.IsEnabled };
        autostart.Click += (_, _) =>
        {
            var error = App.Current.SetAutostart(autostart.IsChecked);
            if (error is not null) App.Current.Notify($"无法修改开机启动：{error}");
        };

        var data = new MenuItem { Header = "打开数据目录" };
        data.Click += (_, _) => App.Current.OpenDataFolder();

        var settings = new MenuItem { Header = "设置…" };
        settings.Click += (_, _) => App.Current.OpenSettings();

        var hideDock = new MenuItem { Header = "隐藏悬浮球" };
        hideDock.Click += (_, _) => App.Current.SetShowDock(false);

        var quit = new MenuItem { Header = "退出 Jump" };
        quit.Click += (_, _) => FadeOutAndQuit();

        var open = new MenuItem
        {
            Header = "打开面板",
            InputGestureText = _settings.HotkeyEnabled ? _settings.Hotkey : string.Empty
        };
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
        foreach (var n in new[] { 3, 4, 5 })
        {
            var c = new MenuItem { Header = n.ToString(), IsCheckable = true, IsChecked = _settings.Columns == n };
            var val = n;
            c.Click += (_, _) => App.Current.SetColumns(val);
            colMenu.Items.Add(c);
        }
        var rowMenu = new MenuItem { Header = "最大行数" };
        foreach (var n in new[] { 2, 3, 4, 5 })
        {
            var c = new MenuItem { Header = n.ToString(), IsCheckable = true, IsChecked = _settings.MaxRows == n };
            var val = n;
            c.Click += (_, _) => App.Current.SetMaxRows(val);
            rowMenu.Items.Add(c);
        }
        layout.Items.Add(colMenu);
        layout.Items.Add(rowMenu);

        menu.Items.Add(open);
        menu.Items.Add(settings);
        menu.Items.Add(hideDock);
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
        if (_holdOpen || PreviewMode) return;
        bool busy = Native.SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
        if (busy == _hiddenForFullscreen) return;
        _hiddenForFullscreen = busy;
        Root.IsHitTestVisible = !busy;
        Root.To(OpacityProperty, busy ? 0 : 1, busy ? 200 : 260);
    }
}
