using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Dukkan.UI;

/// <summary>Presentation-only language state. Switching language flips every bound string and the window flow direction.</summary>
public sealed class Localizer : INotifyPropertyChanged
{
    bool _arabic;

    public static Localizer Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsArabic
    {
        get => _arabic;
        set
        {
            if (_arabic == value) return;
            _arabic = value;
            // Culture drives number/date shaping; digits stay Latin so prices and SKUs read the same in both languages.
            CultureInfo.CurrentUICulture = new CultureInfo(value ? "ar-EG" : "en-US");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    string _storeName = "";

    /// <summary>The shop's own name (Settings); shown under the product logo. Empty until the shop is set up.</summary>
    public string StoreName
    {
        get => _storeName;
        set { if (_storeName == value) return; _storeName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StoreName))); }
    }

    public FlowDirection FlowDirection => _arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public string Currency => this["Currency"];

    public string BackGlyph => _arabic ? Icons.Forward : Icons.Back;

    public string NextGlyph => _arabic ? Icons.ChevronLeft : Icons.ChevronRight;

    public string PrevGlyph => _arabic ? Icons.ChevronRight : Icons.ChevronLeft;

    public string this[string key] => Strings.Get(key, _arabic);

    public bool Has(string key) => Strings.Get(key, false) != key;

    public string Pick(string en, string ar) => _arabic ? ar : en;

    public string Money(decimal value) => $"{Currency} {(value % 1m == 0m ? value.ToString("N0") : value.ToString("N2"))}";
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = Localizer.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

/// <summary>Base for models whose displayed text depends on the language.</summary>
public abstract class LocalizedObject : ObservableObject
{
    protected LocalizedObject() => Localizer.Instance.PropertyChanged += (_, _) => Raise(string.Empty);
}

public sealed class MoneyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? "" : Localizer.Instance.Money(System.Convert.ToDecimal(value));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? "" : System.Convert.ToDecimal(value).ToString("N0", CultureInfo.InvariantCulture);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <summary>Visible when the count is zero (empty states); pass "NotEmpty" to invert.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is int n && n == 0;
        if (parameter is "NotEmpty") empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

public sealed class CategoryLabelConverter : IValueConverter
{
    public static CategoryLabelConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => CategoryNames.Label(value as string ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ChipCheckedConverter : IMultiValueConverter
{
    public static ChipCheckedConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length == 2 && Equals(values[0]?.ToString(), values[1]?.ToString());

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RowNumberConverter : IValueConverter
{
    public static RowNumberConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int i ? (i + 1).ToString(CultureInfo.InvariantCulture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex)) return System.Windows.Media.Brushes.Transparent;
        try { return new System.Windows.Media.BrushConverter().ConvertFromInvariantString(hex) ?? System.Windows.Media.Brushes.Transparent; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            // Non-colour options and old catalog values must never crash a view template.
            return System.Windows.Media.Brushes.Transparent;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
