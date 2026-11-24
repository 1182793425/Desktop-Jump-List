using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DesktopJumpList.Infrastructure;

namespace DesktopJumpList.Services;

/// <summary>
/// Builds the frosted-glass panel backdrop: a screenshot of whatever is behind the
/// panel, blurred with a WPF BlurEffect and frozen into an ImageBrush. The panel tint
/// (ThemeService.PanelBrush) is drawn on top, so the user controls the opacity.
/// </summary>
public static class GlassBackdrop
{
    public static ImageBrush? Capture(Window window, FrameworkElement panel, double blurRadius = 24)
    {
        try
        {
            window.UpdateLayout();
            var w = (int)Math.Round(panel.ActualWidth);
            var h = (int)Math.Round(panel.ActualHeight);
            if (w < 8 || h < 8) return null;

            var dpi = VisualTreeHelper.GetDpi(panel);
            var origin = panel.PointToScreen(new Point(0, 0));
            var pw = Math.Max(8, (int)Math.Round(w * dpi.DpiScaleX));
            var ph = Math.Max(8, (int)Math.Round(h * dpi.DpiScaleY));

            using var shot = new System.Drawing.Bitmap(pw, ph);
            using (var graphics = System.Drawing.Graphics.FromImage(shot))
            {
                graphics.CopyFromScreen(
                    (int)Math.Round(origin.X), (int)Math.Round(origin.Y),
                    0, 0, new System.Drawing.Size(pw, ph),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            var hBitmap = shot.GetHbitmap();
            BitmapSource source;
            try
            {
                source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
            }
            finally
            {
                Native.DeleteObject(hBitmap);
            }

            var image = new Image
            {
                Source = source,
                Width = w,
                Height = h,
                Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = blurRadius, RenderingBias = RenderingBias.Performance }
            };
            image.Measure(new Size(w, h));
            image.Arrange(new Rect(0, 0, w, h));
            image.UpdateLayout();

            var rendered = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(image);
            rendered.Freeze();

            var brush = new ImageBrush(rendered) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }
}
