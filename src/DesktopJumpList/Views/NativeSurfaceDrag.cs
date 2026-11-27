using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopJumpList.Views;

/// <summary>
/// Native-quality surface dragging for borderless windows: WM_NCHITTEST answers HTCAPTION
/// for every point that is not over an interactive control, so Windows itself runs the drag
/// loop. This is the same mechanism a normal title bar uses (PathEntryDialog drags because
/// of it) and, unlike DragMove-inside-MouseLeftButtonDown, it cannot lose to WPF event
/// timing, mouse-capture quirks or a descendant swallowing the raw mouse-down.
/// The window keeps its borderless transparent look and gets no DWM shadow.
/// </summary>
public static class NativeSurfaceDrag
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;

    /// <param name="isInteractive">
    /// True when the point sits on a control that must keep its own mouse handling
    /// (buttons, boxes, sliders, switches, scroll bars...). Everything else drags.
    /// </param>
    public static void Enable(Window window, Func<DependencyObject, bool> isInteractive)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is not HwndSource source) return;
            HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled) =>
            {
                if (msg != WM_NCHITTEST) return IntPtr.Zero;
                handled = true;
                return new IntPtr(IsDraggablePoint(window, isInteractive, lparam) ? HTCAPTION : HTCLIENT);
            };
            source.AddHook(hook);
        };
    }

    private static bool IsDraggablePoint(Window window, Func<DependencyObject, bool> isInteractive, IntPtr lParam)
    {
        try
        {
            // LOWORD/HIWORD as signed shorts: negative values appear on monitors left of /
            // above the primary one.
            var x = (short)((long)lParam & 0xFFFF);
            var y = (short)(((long)lParam >> 16) & 0xFFFF);

            if (PresentationSource.FromVisual(window) is not HwndSource source
                || source.CompositionTarget is null) return true;
            var toDips = source.CompositionTarget.TransformToDevice;
            toDips.Invert();
            var pt = toDips.Transform(new Point(x, y));

            // Transparent margin outside the rounded content border: drag is fine there.
            if (pt.X < 0 || pt.Y < 0 || pt.X > window.ActualWidth || pt.Y > window.ActualHeight) return true;

            var hit = window.InputHitTest(pt) as DependencyObject;
            return hit is null || !isInteractive(hit);
        }
        catch
        {
            return false; // safest default: the control under the cursor keeps the click
        }
    }
}
