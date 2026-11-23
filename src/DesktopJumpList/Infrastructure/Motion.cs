using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopJumpList.Infrastructure;

/// <summary>
/// Tiny animation vocabulary shared by every surface, so timing stays consistent.
/// Rule of thumb: entering 180–240ms ease-out, leaving 120–160ms ease-in. Nothing ever pops.
/// </summary>
public static class Motion
{
    public static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction OutStrong = new QuinticEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction In = new CubicEase { EasingMode = EasingMode.EaseIn };
    public static readonly IEasingFunction InOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

    static Motion()
    {
        ((Freezable)Out).Freeze();
        ((Freezable)OutStrong).Freeze();
        ((Freezable)In).Freeze();
        ((Freezable)InOut).Freeze();
    }

    /// <summary>Animates from the *current* value, so interrupted animations never jump.</summary>
    public static void To(this IAnimatable target, DependencyProperty dp, double to, double ms,
        IEasingFunction? ease = null, double delayMs = 0, Action? completed = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? Out,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            FillBehavior = FillBehavior.HoldEnd
        };
        if (completed is not null) anim.Completed += (_, _) => completed();
        target.BeginAnimation(dp, anim, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Same as <see cref="To"/> but with an explicit start value.</summary>
    public static void FromTo(this IAnimatable target, DependencyProperty dp, double from, double to, double ms,
        IEasingFunction? ease = null, double delayMs = 0, Action? completed = null)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? Out,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            FillBehavior = FillBehavior.HoldEnd
        };
        if (completed is not null) anim.Completed += (_, _) => completed();
        target.BeginAnimation(dp, anim, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Stops any running animation and pins the property to a value.</summary>
    public static void Set(this IAnimatable target, DependencyProperty dp, double value)
    {
        target.BeginAnimation(dp, null);
        if (target is DependencyObject d) d.SetValue(dp, value);
    }
}

/// <summary>
/// Moves a top-level window smoothly. WPF cannot animate Window.Left/Top through the
/// animation system reliably (each property sets position separately and tears), so this
/// drives both together from CompositionTarget.Rendering, once per frame.
/// </summary>
public sealed class WindowMover
{
    private readonly Window _window;
    private readonly Stopwatch _clock = new();
    private double _fromX, _fromY, _toX, _toY, _ms;
    private Action? _done;
    private bool _running;

    public WindowMover(Window window) => _window = window;

    public bool IsRunning => _running;

    public void MoveTo(double left, double top, double ms, Action? done = null)
    {
        Stop();
        _fromX = _window.Left; _fromY = _window.Top;
        _toX = left; _toY = top; _ms = Math.Max(1, ms);
        _done = done;
        _clock.Restart();
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!_running) return;
        CompositionTarget.Rendering -= OnFrame;
        _running = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / _ms);
        var k = 1 - Math.Pow(1 - t, 3); // cubic ease-out
        _window.Left = _fromX + (_toX - _fromX) * k;
        _window.Top = _fromY + (_toY - _fromY) * k;
        if (t >= 1)
        {
            Stop();
            var done = _done; _done = null;
            done?.Invoke();
        }
    }
}
