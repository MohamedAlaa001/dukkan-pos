using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dukkan.UI;

/// <summary>
/// Interface icon drawn from the bundled artwork in Assets/UiIcons. The artwork is a black mask, painted in the
/// element's Foreground, so the same icon works on navy, orange, grey or red. <see cref="Glyph"/> takes the same value as the
/// old <see cref="Icons"/> constants, so call sites did not have to change.
/// </summary>
public sealed class GlyphIcon : FrameworkElement
{
    static readonly Dictionary<string, string> Names = BuildNames();
    static readonly Dictionary<string, ImageSource?> Cache = [];

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(GlyphIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(GlyphIcon), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(GlyphIcon), new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    static GlyphIcon()
    {
        // Pictures must not mirror in right-to-left screens; directional icons are chosen by the caller (BackGlyph/NextGlyph).
        FlowDirectionProperty.OverrideMetadata(typeof(GlyphIcon), new FrameworkPropertyMetadata(FlowDirection.LeftToRight));
    }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    static Dictionary<string, string> BuildNames()
    {
        var map = new Dictionary<string, string>();
        foreach (var f in typeof(Icons).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            if (f.IsLiteral && f.GetRawConstantValue() is string v && v.Length > 0) map.TryAdd(v, f.Name.ToLowerInvariant());
        if (map.TryGetValue(Icons.Shop, out _)) map[Icons.Shop] = "bag";
        return map;
    }

    static ImageSource? Source(string? glyph)
    {
        if (string.IsNullOrEmpty(glyph) || !Names.TryGetValue(glyph, out var name)) return null;
        if (Cache.TryGetValue(name, out var hit)) return hit;
        ImageSource? img = null;
        try
        {
            var bmp = new BitmapImage(new Uri($"pack://application:,,,/Assets/UiIcons/{name}.png", UriKind.Absolute));
            bmp.Freeze();
            img = bmp;
        }
        catch (Exception) { /* a missing picture simply draws nothing */ }
        return Cache[name] = img;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = FontSize * 1.15;
        return new Size(double.IsNaN(Width) ? s : Width, double.IsNaN(Height) ? s : Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Source(Glyph) is not { } img || ActualWidth <= 0 || ActualHeight <= 0) return;
        var side = Math.Min(ActualWidth, ActualHeight);
        var rect = new Rect((ActualWidth - side) / 2, (ActualHeight - side) / 2, side, side);
        dc.PushOpacityMask(new ImageBrush(img) { Stretch = Stretch.Uniform });
        dc.DrawRectangle(Foreground, null, rect);
        dc.Pop();
    }
}
