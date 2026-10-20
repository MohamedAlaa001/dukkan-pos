using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Dukkan.UI;

public sealed record ChartPoint(string Label, double Value);

/// <summary>Shared axis helpers for the two chart controls (drawn in OnRender, so they scale crisply at any DPI).</summary>
public abstract class ChartBase : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable), typeof(ChartBase),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(ChartBase),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected IReadOnlyList<ChartPoint> Points => Items?.Cast<ChartPoint>().ToList() ?? [];

    protected Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected static double NiceMax(double max)
    {
        if (max <= 0) return 1;
        var step = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var m in new[] { 1d, 2d, 2.5d, 5d, 10d })
            if (max <= m * step) return m * step;
        return max;
    }

    protected static string Axis(double v) => v >= 1000 ? $"{v / 1000:0.#}K" : $"{v:0}";

    /// <summary>Draws horizontal grid lines with y-axis labels; returns the plot rectangle.</summary>
    protected Rect DrawAxes(DrawingContext dc, double max, int ticks)
    {
        var grid = new Pen(Res("BorderBrush", Brushes.LightGray), 1);
        var muted = Res("TextSecondaryBrush", Brushes.Gray);
        var labelW = Text(Axis(max), 12, muted).Width + 12;
        var plot = new Rect(labelW, 8, Math.Max(0, ActualWidth - labelW - 4), Math.Max(0, ActualHeight - 8 - 26));
        for (var i = 0; i <= ticks; i++)
        {
            var v = max * i / ticks;
            var y = plot.Bottom - plot.Height * i / ticks;
            var snapped = Math.Round(y) + 0.5;
            dc.DrawLine(i == 0 ? new Pen(Res("BorderBrush", Brushes.LightGray), 1) : new Pen(Res("BorderBrush", Brushes.LightGray) is SolidColorBrush b ? new SolidColorBrush(Color.FromArgb(120, b.Color.R, b.Color.G, b.Color.B)) : Brushes.LightGray, 1),
                new Point(plot.Left, snapped), new Point(plot.Right, snapped));
            var t = Text(Axis(v), 12, muted);
            dc.DrawText(t, new Point(labelW - 8 - t.Width, y - t.Height / 2));
        }

        _ = grid;
        return plot;
    }
}

public sealed class BarChart : ChartBase
{
    protected override void OnRender(DrawingContext dc)
    {
        var pts = Points;
        if (pts.Count == 0 || ActualWidth < 40 || ActualHeight < 40) return;
        var max = NiceMax(pts.Max(p => p.Value));
        var plot = DrawAxes(dc, max, 4);
        var muted = Res("TextSecondaryBrush", Brushes.Gray);
        var slot = plot.Width / pts.Count;
        var barW = Math.Min(slot * 0.5, 44);
        for (var i = 0; i < pts.Count; i++)
        {
            var cx = plot.Left + slot * (i + 0.5);
            var h = plot.Height * pts[i].Value / max;
            var rect = new Rect(cx - barW / 2, plot.Bottom - h, barW, h);
            var geo = new RectangleGeometry(rect, 3, 3);
            dc.DrawGeometry(BarBrush, null, geo);
            var t = Text(pts[i].Label, 12, muted);
            dc.DrawText(t, new Point(cx - t.Width / 2, plot.Bottom + 6));
        }
    }
}

public sealed class AreaChart : ChartBase
{
    protected override void OnRender(DrawingContext dc)
    {
        var pts = Points;
        if (pts.Count < 2 || ActualWidth < 40 || ActualHeight < 40) return;
        var max = NiceMax(pts.Max(p => p.Value));
        var plot = DrawAxes(dc, max, 3);
        var muted = Res("TextSecondaryBrush", Brushes.Gray);
        var orange = Res("OrangeBrush", Brushes.Orange) as SolidColorBrush ?? Brushes.Orange;

        var xy = pts.Select((p, i) => new Point(plot.Left + plot.Width * i / (pts.Count - 1), plot.Bottom - plot.Height * p.Value / max)).ToList();
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(xy[0].X, plot.Bottom), true, true);
            foreach (var p in xy) ctx.LineTo(p, true, false);
            ctx.LineTo(new Point(xy[^1].X, plot.Bottom), true, false);
        }

        area.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(120, orange.Color.R, orange.Color.G, orange.Color.B), Color.FromArgb(10, orange.Color.R, orange.Color.G, orange.Color.B), 90);
        dc.DrawGeometry(fill, null, area);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(xy[0], false, false);
            foreach (var p in xy.Skip(1)) ctx.LineTo(p, true, true);
        }

        line.Freeze();
        dc.DrawGeometry(null, new Pen(orange, 2) { LineJoin = PenLineJoin.Round }, line);

        // Label roughly every second point so labels never collide.
        var stride = Math.Max(1, (int)Math.Ceiling(pts.Count / Math.Max(1d, plot.Width / 70)));
        for (var i = 0; i < pts.Count; i += stride)
        {
            var t = Text(pts[i].Label, 12, muted);
            dc.DrawText(t, new Point(Math.Clamp(xy[i].X - t.Width / 2, 0, ActualWidth - t.Width), plot.Bottom + 6));
        }
    }
}
