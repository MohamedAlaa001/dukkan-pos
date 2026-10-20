using System.Windows;
using System.Windows.Controls;

namespace Dukkan.UI;

/// <summary>Rotating arc used for loading states.</summary>
public sealed class Spinner : Control
{
    static Spinner() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Spinner), new FrameworkPropertyMetadata(typeof(Spinner)));
}

/// <summary>Empty list/search state: icon, title, message and an optional action in Content.</summary>
public sealed class EmptyState : ContentControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata(Icons.Search));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState));

    static EmptyState() => DefaultStyleKeyProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
}

/// <summary>Summary card: caption, big value, optional delta line, tinted icon tile.</summary>
public sealed class StatCard : ContentControl
{
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(StatCard));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(StatCard));
    public static readonly DependencyProperty DeltaProperty = DependencyProperty.Register(nameof(Delta), typeof(string), typeof(StatCard));
    public static readonly DependencyProperty DeltaKindProperty = DependencyProperty.Register(nameof(DeltaKind), typeof(BadgeKind), typeof(StatCard), new PropertyMetadata(BadgeKind.Success));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(StatCard));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(System.Windows.Media.Brush), typeof(StatCard));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(System.Windows.Media.Brush), typeof(StatCard));

    static StatCard() => DefaultStyleKeyProperty.OverrideMetadata(typeof(StatCard), new FrameworkPropertyMetadata(typeof(StatCard)));

    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public string Delta { get => (string)GetValue(DeltaProperty); set => SetValue(DeltaProperty, value); }

    public BadgeKind DeltaKind { get => (BadgeKind)GetValue(DeltaKindProperty); set => SetValue(DeltaKindProperty, value); }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public System.Windows.Media.Brush Tint { get => (System.Windows.Media.Brush)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    public System.Windows.Media.Brush Accent { get => (System.Windows.Media.Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
}
