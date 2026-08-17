using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BeamSplit.Views;

/// <summary>A lightweight vector dial: crisp at every DPI and cheap enough to redraw live.</summary>
public sealed class DashGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(DashGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(DashGauge),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(DashGauge),
        new FrameworkPropertyMetadata("GAUGE", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(DashGauge),
        new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(DashGauge),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 170 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 170 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 30) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2 + size * .03);
        var radius = size * .39;
        var face = new RadialGradientBrush(Color.FromRgb(35, 39, 48), Color.FromRgb(7, 9, 13));
        dc.DrawEllipse(face, new Pen(new SolidColorBrush(Color.FromRgb(64, 70, 82)), 1.3), center,
            radius + size * .095, radius + size * .095);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(8, 10, 14)), null, center, radius + size * .035, radius + size * .035);

        const double start = 135;
        const double sweep = 270;
        var trackPen = new Pen(new SolidColorBrush(Color.FromRgb(44, 49, 59)), Math.Max(5, size * .045))
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, trackPen, Arc(center, radius, start, sweep));
        var ratio = Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);
        if (ratio > .005)
        {
            var glow = Accent.Clone(); glow.Opacity = .24;
            dc.DrawGeometry(null, new Pen(glow, Math.Max(10, size * .08)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Arc(center, radius, start, sweep * ratio));
            dc.DrawGeometry(null, new Pen(Accent, Math.Max(5, size * .045)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Arc(center, radius, start, sweep * ratio));
        }

        for (var i = 0; i <= 10; i++)
        {
            var angle = start + sweep * i / 10d;
            var major = i % 5 == 0;
            var outer = Polar(center, radius - size * .055, angle);
            var inner = Polar(center, radius - size * (major ? .13 : .095), angle);
            dc.DrawLine(new Pen(major ? Brushes.White : new SolidColorBrush(Color.FromRgb(102, 108, 121)), major ? 2 : 1), inner, outer);
        }

        var needleAngle = start + sweep * ratio;
        var needle = Polar(center, radius * .78, needleAngle);
        dc.DrawLine(new Pen(Accent, Math.Max(2, size * .016)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, center, needle);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(220, 224, 230)), new Pen(Brushes.Black, 2), center, size * .035, size * .035);

        // Keep the numeric readout and caption in separate visual bands. The old
        // offsets left only a few pixels between their line boxes at common DPI.
        DrawText(dc, ValueText, size * .135, FontWeights.SemiBold, Brushes.White,
            new Point(center.X, center.Y + size * .11));
        DrawText(dc, Label, size * .056, FontWeights.Bold, new SolidColorBrush(Color.FromRgb(139, 147, 163)),
            new Point(center.X, center.Y + size * .31));
    }

    private static void DrawText(DrawingContext dc, string text, double size, FontWeight weight, Brush brush, Point center)
    {
        var pixelsPerDip = Application.Current?.MainWindow is Visual visual
            ? VisualTreeHelper.GetDpi(visual).PixelsPerDip : 1d;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            Math.Max(8, size), brush, pixelsPerDip);
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private static Point Polar(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180d;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }

    private static Geometry Arc(Point center, double radius, double start, double sweep)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(Polar(center, radius, start), false, false);
        ctx.ArcTo(Polar(center, radius, start + sweep), new Size(radius, radius), 0,
            Math.Abs(sweep) > 180, sweep >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
        geometry.Freeze();
        return geometry;
    }
}
