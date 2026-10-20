using LocalCashier.Persistence;

namespace Dukkan.UI;

/// <summary>Display names and pictures for shop-defined categories. Built-in keys from older shops (TShirts, Pants…) keep their translations.</summary>
public static class CategoryNames
{
    static Dictionary<string, string?> _alt = [];
    static Dictionary<string, string?> _icon = [];

    public static void Refresh(IEnumerable<CategoryRow> rows)
    {
        var list = rows.ToList();
        _alt = list.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First().NameAlt);
        _icon = list.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First().Icon);
    }

    public static string Label(string name)
    {
        var l = Localizer.Instance;
        if (string.IsNullOrWhiteSpace(name)) return l["Cat_Uncategorized"];
        if (l.Has("Cat_" + name)) return l["Cat_" + name];
        return l.IsArabic && _alt.TryGetValue(name, out var alt) && !string.IsNullOrWhiteSpace(alt) ? alt : name;
    }

    public static string? Icon(string name) => _icon.TryGetValue(name, out var i) ? i : null;
}
