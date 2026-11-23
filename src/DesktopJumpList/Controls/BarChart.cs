using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopJumpList.Models;

namespace DesktopJumpList.Controls;

/// <summary>A small, hand-drawn 7-day bar chart — no chart library, no default WPF chrome.</summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable<DayBar>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.CornflowerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MutedTextBrushProperty = DependencyProperty.Register(
        nameof(MutedTextBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(BarChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly FontFamily ChartFont = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    private INotifyCollectionChanged? _observable;

    public BarChart()
    {
        Loaded += (_, _) => Animate();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Animate();
        };
    }

    public IEnumerable<DayBar>? Items
    {
        get => (IEnumerable<DayBar>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public Brush MutedTextBrush
    {
        get => (Brush)GetValue(MutedTextBrushProperty);
        set => SetValue(MutedTextBrushProperty, value);
    }

    public double Reveal
    {
        get => (double)GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not BarChart chart) return;
        if (chart._observable is not null) chart._observable.CollectionChanged -= chart.OnItemsChanged;
        chart._observable = e.NewValue as INotifyCollectionChanged;
        if (chart._observable is not null) chart._observable.CollectionChanged += chart.OnItemsChanged;
        chart.InvalidateVisual();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private void Animate()
    {
        BeginAnimation(RevealProperty, null);
        SetCurrentValue(RevealProperty, 0.0);
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(480))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            BeginTime = TimeSpan.FromMilliseconds(40)
        };
        BeginAnimation(RevealProperty, animation);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 150 : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Items is null) return;
        var items = Items.ToList();
        if (items.Count == 0 || ActualWidth < 30 || ActualHeight < 50) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = ActualWidth;
        var height = ActualHeight;
        const double labelArea = 20;
        const double topArea = 18;
        var baseline = height - labelArea;
        var plotHeight = baseline - topArea;
        var slot = width / items.Count;
        var max = Math.Max(1, items.Max(item => item.Count));
        var barWidth = Math.Min(18, slot * 0.42);
        var reveal = Math.Clamp(Reveal, 0, 1);

        var gridPen = new Pen(TrackBrush, 1) { DashStyle = new DashStyle([2, 4], 0), Brush = TrackBrush };
        gridPen.Freeze();
        for (var i = 0; i < 3; i++)
        {
            var y = topArea + plotHeight * i / 2.0;
            dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var center = slot * (i + 0.5);
            var ratio = item.Count / (double)max;
            var barHeight = Math.Max(item.Count > 0 ? 3 : 0, plotHeight * ratio * reveal);
            var track = new Rect(center - barWidth / 2, topArea, barWidth, plotHeight);
            dc.PushOpacity(0.26);
            dc.DrawRoundedRectangle(TrackBrush, null, track, barWidth / 2, barWidth / 2);
            dc.Pop();

            if (barHeight > 0)
            {
                var bar = new Rect(center - barWidth / 2, baseline - barHeight, barWidth, barHeight);
                Brush fill = item.IsToday ? BarBrush : WithOpacity(BarBrush, 0.28);
                dc.DrawGeometry(fill, null, TopRounded(bar, barWidth / 2));
            }

            var countText = Text(item.Count.ToString(CultureInfo.InvariantCulture), 9.5,
                item.IsToday ? TextBrush : MutedTextBrush, dpi);
            var countY = Math.Max(0, baseline - barHeight - countText.Height - 3);
            dc.DrawText(countText, new Point(center - countText.Width / 2, countY));

            var dayText = Text(item.Label, 10, item.IsToday ? TextBrush : MutedTextBrush, dpi);
            if (item.IsToday) dayText.SetFontWeight(FontWeights.SemiBold);
            dc.DrawText(dayText, new Point(center - dayText.Width / 2, baseline + 5));
        }
    }

    private static Brush WithOpacity(Brush source, double opacity)
    {
        if (source is SolidColorBrush solid)
        {
            var brush = new SolidColorBrush(solid.Color) { Opacity = opacity };
            brush.Freeze();
            return brush;
        }

        var copy = source.CloneCurrentValue();
        copy.Opacity = opacity;
        copy.Freeze();
        return copy;
    }

    private static Geometry TopRounded(Rect rect, double radius)
    {
        radius = Math.Min(radius, Math.Min(rect.Width, rect.Height));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(rect.Left, rect.Bottom), true, true);
            context.LineTo(new Point(rect.Left, rect.Top + radius), true, false);
            context.ArcTo(new Point(rect.Left + radius, rect.Top), new Size(radius, radius), 0, false,
                SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(rect.Right - radius, rect.Top), true, false);
            context.ArcTo(new Point(rect.Right, rect.Top + radius), new Size(radius, radius), 0, false,
                SweepDirection.Clockwise, true, false);
            context.LineTo(new Point(rect.Right, rect.Bottom), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private FormattedText Text(string value, double size, Brush brush, double dpi) => new(
        value,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(ChartFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        size,
        brush,
        dpi);
}
