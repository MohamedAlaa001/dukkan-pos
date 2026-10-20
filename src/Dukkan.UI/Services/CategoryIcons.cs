using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalCashier.Persistence;

namespace Dukkan.UI;

/// <summary>Bundled pictures shown for products without a photo, chosen by category.</summary>
public static class CategoryIcons
{
    static readonly Dictionary<string, ImageSource> Cache = [];

    /// <summary>On by default: every product without a photo shows its category picture.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Names of all bundled pictures, in picker order.</summary>
    public static IReadOnlyList<string> Names => BusinessPresets.IconNames;

    static readonly Dictionary<string, string> Legacy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tshirts"] = "shirt", ["pants"] = "jeans", ["jackets"] = "coat", ["hoodies"] = "coat", ["shirts"] = "shirt", ["others"] = "gift",
    };

    public static ImageSource? Get(string? name)
    {
        if (string.IsNullOrEmpty(name) || !Names.Contains(name)) return null;
        lock(Cache)
        {
            if (Cache.TryGetValue(name, out var hit)) return hit;
            var assembly=typeof(CategoryIcons).Assembly.GetName().Name;
            var img = new BitmapImage(new Uri($"pack://application:,,,/{assembly};component/Assets/Icons/{name}.png", UriKind.Absolute));
            img.Freeze();
            return Cache[name] = img;
        }
    }

    public static ImageSource? For(string? category)
    {
        if (!Enabled) return null;
        var key = category ?? "";
        var name = CategoryNames.Icon(key) ?? (Legacy.TryGetValue(key, out var l) ? l : BusinessPresets.GuessIcon(key));
        return Get(name) ?? Get("basket");
    }
}
