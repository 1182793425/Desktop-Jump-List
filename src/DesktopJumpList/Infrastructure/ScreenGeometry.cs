using System.Windows;
using System.Windows.Media;

namespace DesktopJumpList.Infrastructure;

/// <summary>
/// Resolves monitor work areas in WPF device-independent units, so the dock and the
/// launcher land on the monitor the pointer is actually on (multi-monitor aware).
/// </summary>
public static class ScreenGeometry
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Work area of the monitor under the mouse pointer, in DIPs of <paramref name="visual"/>.</summary>
    public static Rect WorkAreaAtCursor(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        if (Native.GetCursorPos(out var p))
            return WorkAreaForPhysicalPoint(p.X, p.Y, dpi.DpiScaleX, dpi.DpiScaleY);
        return SystemParameters.WorkArea;
    }

    /// <summary>Work area of the monitor containing a DIP point, in DIPs of <paramref name="visual"/>.</summary>
    public static Rect WorkAreaAtPoint(double left, double top, Visual visual)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top))
            return WorkAreaAtCursor(visual);

        var dpi = VisualTreeHelper.GetDpi(visual);
        var x = (int)Math.Round(left * dpi.DpiScaleX);
        var y = (int)Math.Round(top * dpi.DpiScaleY);
        return WorkAreaForPhysicalPoint(x, y, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private static Rect WorkAreaForPhysicalPoint(int x, int y, double scaleX, double scaleY)
    {
        try
        {
            var h = Native.MonitorFromPoint(new Native.POINT { X = x, Y = y }, MonitorDefaultToNearest);
            var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (h != IntPtr.Zero && Native.GetMonitorInfo(h, ref info))
            {
                return new Rect(
                    info.rcWork.Left / scaleX,
                    info.rcWork.Top / scaleY,
                    (info.rcWork.Right - info.rcWork.Left) / scaleX,
                    (info.rcWork.Bottom - info.rcWork.Top) / scaleY);
            }
        }
        catch { }
        return SystemParameters.WorkArea;
    }
}
