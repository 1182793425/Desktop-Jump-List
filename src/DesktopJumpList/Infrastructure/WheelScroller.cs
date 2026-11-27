using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopJumpList.Infrastructure;

/// <summary>
/// Makes the wheel scroll a list even when the pointer is over a child that would
/// normally swallow the event (or when the list itself has no internal scroll viewer).
/// A fixed ~64 px per notch feels right for the 96 px launcher tiles.
/// </summary>
public static class WheelScroller
{
    private const double StepDips = 68;

    public static void Attach(Window window, Func<ScrollViewer?> viewer)
    {
        window.PreviewMouseWheel += (_, e) =>
        {
            var sv = viewer();
            if (sv is null || sv.Visibility != Visibility.Visible) return;
            var range = sv.ScrollableHeight;
            if (range <= 0.5) return;

            var direction = e.Delta > 0 ? -1 : 1;
            var target = Math.Clamp(sv.VerticalOffset + direction * StepDips, 0, range);
            if (Math.Abs(target - sv.VerticalOffset) < 0.5) return;

            sv.ScrollToVerticalOffset(target);
            e.Handled = true;
        };
    }

    /// <summary>
    /// Wheeling anywhere inside <paramref name="host"/> scrolls <paramref name="viewer"/> — the
    /// list plus its title row, status line and padding. A bounded list sitting inside a scrolling
    /// page otherwise has dead zones around it: the wheel falls through to the page, the page
    /// moves instead of the list, and the list reads as "won't scroll". At either end the event
    /// is left alone, so the page keeps taking over exactly where the list runs out.
    /// </summary>
    public static void RouteWheelTo(UIElement host, ScrollViewer viewer)
    {
        host.PreviewMouseWheel += (_, e) =>
        {
            var range = viewer.ScrollableHeight;
            if (range <= 0.5) return;

            var target = Math.Clamp(viewer.VerticalOffset + (e.Delta > 0 ? -StepDips : StepDips), 0, range);
            if (Math.Abs(target - viewer.VerticalOffset) < 0.5) return;

            viewer.ScrollToVerticalOffset(target);
            e.Handled = true;
        };
    }

    /// <summary>
    /// Window-level safety net: whatever element the wheel lands on, the nearest thing that can
    /// actually scroll under the pointer wins — unless that is the page itself, which is left to
    /// WPF so ordinary page scrolling keeps its native feel.
    /// </summary>
    public static void AttachNearest(Window window, ScrollViewer page)
    {
        window.PreviewMouseWheel += (_, e) =>
        {
            var sv = NearestScrollable(e.OriginalSource as DependencyObject);
            if (sv is null || ReferenceEquals(sv, page)) return;

            var range = sv.ScrollableHeight;
            if (range <= 0.5) return;
            var target = Math.Clamp(sv.VerticalOffset + (e.Delta > 0 ? -StepDips : StepDips), 0, range);
            if (Math.Abs(target - sv.VerticalOffset) < 0.5) return;

            sv.ScrollToVerticalOffset(target);
            e.Handled = true;
        };
    }

    private static ScrollViewer? NearestScrollable(DependencyObject? node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer viewer && viewer.ScrollableHeight > 0.5) return viewer;
        }
        return null;
    }
}
