using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopJumpList.Models;

namespace DesktopJumpList.Controls;

public sealed class DonutChart : FrameworkElement
{
    private INotifyCollectionChanged? _observable;

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable<DonutSegment>), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(DonutChart),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(DonutChart),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(DonutChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public DonutChart()
    {
        Loaded += (_, _) => Animate();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Animate();
        };
    }

    public IEnumerable<DonutSegment>? Segments
    {
        get => (IEnumerable<DonutSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public double Reveal
    {
        get => (double)GetValue(RevealProperty);
        set => SetValue(RevealProperty, value);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DonutChart chart) return;
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
        BeginAnimation(RevealProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(560))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            BeginTime = TimeSpan.FromMilliseconds(60)
        });
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 120 : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Segments is null) return;
        var segments = Segments.Where(segment => segment.Value > 0).ToList();
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - RingThickness / 2 - 1;
        var trackPen = new Pen(TrackBrush, RingThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        trackPen.Freeze();
        dc.PushOpacity(0.55);
        dc.DrawEllipse(null, trackPen, center, radius, radius);
        dc.Pop();

        var total = segments.Sum(segment => segment.Value);
        if (total <= 0) return;

        var reveal = Math.Clamp(Reveal, 0, 1);
        if (segments.Count == 1)
        {
            var pen = new Pen(segments[0].Brush, RingThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.PushOpacity(reveal);
            dc.DrawEllipse(null, pen, center, radius, radius);
            dc.Pop();
            return;
        }

        const double gap = 7.0;
        var angle = -90.0;
        foreach (var segment in segments)
        {
            var share = segment.Value / (double)total;
            var sweep = Math.Max(0, share * 360 - gap);
            var pen = new Pen(segment.Brush, RingThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            if (sweep > 0.5)
            {
                dc.DrawGeometry(null, pen, Arc(center, radius, angle, sweep * reveal));
            }

            angle += share * 360;
        }
    }

    private static Geometry Arc(Point center, double radius, double startAngle, double sweep)
    {
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweep);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
