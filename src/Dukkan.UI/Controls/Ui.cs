using System.Windows;
using System.Windows.Controls;

namespace Dukkan.UI;

/// <summary>Attached properties shared by the input styles (placeholder text and a leading icon glyph).</summary>
public static class Ui
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new PropertyMetadata("", OnPlaceholderChanged));

    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Ui), new PropertyMetadata(""));

    static readonly DependencyPropertyKey HasValuePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "HasValue", typeof(bool), typeof(Ui), new PropertyMetadata(false));

    public static readonly DependencyProperty HasValueProperty = HasValuePropertyKey.DependencyProperty;

    // PasswordBox.Password cannot be bound; this attached property mirrors it two-way without exposing the value anywhere else.
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(Ui),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordPropertyChanged));

    static bool _updatingPassword;

    public static string GetPassword(DependencyObject d) => (string)d.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject d, string v) => d.SetValue(PasswordProperty, v);

    static void OnPasswordPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox pb) return;
        pb.PasswordChanged -= OnBoundPasswordChanged;
        if (!_updatingPassword && pb.Password != ((string?)e.NewValue ?? "")) pb.Password = (string?)e.NewValue ?? "";
        pb.PasswordChanged += OnBoundPasswordChanged;
    }

    static void OnBoundPasswordChanged(object sender, RoutedEventArgs e)
    {
        var pb = (PasswordBox)sender;
        _updatingPassword = true;
        try { SetPassword(pb, pb.Password); }
        finally { _updatingPassword = false; }
    }

    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject d, string v) => d.SetValue(PlaceholderProperty, v);

    public static string GetIcon(DependencyObject d) => (string)d.GetValue(IconProperty);

    public static void SetIcon(DependencyObject d, string v) => d.SetValue(IconProperty, v);

    public static bool GetHasValue(DependencyObject d) => (bool)d.GetValue(HasValueProperty);

    static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox pb) return;
        pb.PasswordChanged -= OnPasswordChanged;
        pb.PasswordChanged += OnPasswordChanged;
        pb.SetValue(HasValuePropertyKey, pb.Password.Length > 0);
    }

    static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var pb = (PasswordBox)sender;
        pb.SetValue(HasValuePropertyKey, pb.Password.Length > 0);
    }
}

public enum BadgeKind { Neutral, Success, Warning, Danger, Info }

/// <summary>Small status pill ("Active", "Low Stock", "Sale"…).</summary>
public sealed class Badge : ContentControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(BadgeKind), typeof(Badge), new PropertyMetadata(BadgeKind.Neutral));

    static Badge() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Badge), new FrameworkPropertyMetadata(typeof(Badge)));

    public BadgeKind Kind
    {
        get => (BadgeKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}

public enum GarmentKind { TShirt, Polo, Jeans, Jacket, Hoodie, Shorts, Sweater, Cap, Shirt, Chinos }

/// <summary>Simple vector clothing placeholder; the colour is the garment colour.</summary>
public sealed class GarmentThumb : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(GarmentKind), typeof(GarmentThumb), new PropertyMetadata(GarmentKind.TShirt));

    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image), typeof(System.Windows.Media.ImageSource), typeof(GarmentThumb), new PropertyMetadata(null, (d, e) => d.SetValue(HasImagePropertyKey, e.NewValue is not null)));

    static readonly DependencyPropertyKey HasImagePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasImage), typeof(bool), typeof(GarmentThumb), new PropertyMetadata(false));

    public static readonly DependencyProperty HasImageProperty = HasImagePropertyKey.DependencyProperty;

    /// <summary>A product photo; when set it replaces the vector placeholder.</summary>
    public System.Windows.Media.ImageSource? Image { get => (System.Windows.Media.ImageSource?)GetValue(ImageProperty); set => SetValue(ImageProperty, value); }

    public bool HasImage => (bool)GetValue(HasImageProperty);

    static GarmentThumb() => DefaultStyleKeyProperty.OverrideMetadata(typeof(GarmentThumb), new FrameworkPropertyMetadata(typeof(GarmentThumb)));

    public GarmentKind Kind
    {
        get => (GarmentKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
