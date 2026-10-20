using System.Globalization;
using LocalCashier.Persistence;

namespace Dukkan.UI;

// Presentation models: thin, language-aware wrappers over the rows returned by the services.

/// <summary>A catalog style ("T-Shirt") with its variants rolled up; used by the product list and POS grid.</summary>
public sealed class StyleItem : LocalizedObject
{
    bool _isChecked;

    public StyleItem(StyleRow r)
    {
        Id = r.Id; NameEn = r.NameEn; NameAlt = r.NameAlt; CategoryKey = r.CategoryKey; BasePrice = r.BasePrice;
        TotalStock = r.TotalStock; VariantCount = r.VariantCount; LowStockAlert = r.LowStockAlert; Active = r.Active;
        ColorHex = r.FirstColorHex ?? "#1F2937";
        Kind = Enum.TryParse<GarmentKind>(r.Kind, out var k) ? k : GarmentKind.TShirt;
        State = r.State;
        HasImage = r.HasImage;
    }

    public bool HasImage { get; }
    /// <summary>Product photo, or null to show the garment placeholder.</summary>
    public System.Windows.Media.ImageSource? Image => (HasImage ? ProductImages.Get(Id) : null) ?? CategoryIcons.For(CategoryKey);
    public long Id { get; }
    public string NameEn { get; }
    public string? NameAlt { get; }
    public string CategoryKey { get; }
    public decimal BasePrice { get; }
    public decimal TotalStock { get; }
    public int VariantCount { get; }
    public int LowStockAlert { get; }
    public bool Active { get; }
    public string ColorHex { get; }
    public GarmentKind Kind { get; }
    public StockStatus State { get; }
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    public string Name => string.IsNullOrWhiteSpace(NameAlt) ? NameEn : Localizer.Instance.Pick(NameEn, NameAlt);
    public string Category => CategoryNames.Label(CategoryKey);
    public string Status => Localizer.Instance["Status_" + State];
    public BadgeKind StatusKind => State switch { StockStatus.Active => BadgeKind.Success, StockStatus.LowStock => BadgeKind.Warning, _ => BadgeKind.Danger };
    public bool StockIsCritical => TotalStock <= LowStockAlert;
    public string VariantsText => string.Format(Localizer.Instance["Prod_Variants"], VariantCount);
}

/// <summary>One sellable size/color of a style.</summary>
public sealed class VariantItem : LocalizedObject
{
    public VariantItem(VariantRow r, string styleNameEn, string? styleNameAlt, GarmentKind kind)
    {
        ProductId = r.ProductId; StyleId = r.StyleId; Sku = r.Sku; Barcode = r.Barcode; Size = r.Size ?? ""; ColorHex = r.ColorHex ?? "#9CA3AF";
        ColorNameEn = r.ColorNameEn ?? ""; ColorNameAlt = r.ColorNameAlt ?? ""; Price = r.Price; AvgCost = r.AvgCost; Stock = r.Stock; Active = r.Active;
        Unit = r.Unit; AllowDecimal = r.AllowDecimal;
        StyleNameEn = styleNameEn; StyleNameAlt = styleNameAlt; Kind = kind;
    }

    public long ProductId { get; }
    public long StyleId { get; }
    public string Sku { get; }
    public string? Barcode { get; }
    public string Size { get; }
    public string ColorHex { get; }
    public string ColorNameEn { get; }
    public string ColorNameAlt { get; }
    public decimal Price { get; }
    public decimal AvgCost { get; }
    public decimal Stock { get; }
    public string Unit { get; } = "pc";
    public bool AllowDecimal { get; }
    public string UnitLabel => Unit == "pc" ? "" : Unit;
    public bool Active { get; }
    public string StyleNameEn { get; }
    public string? StyleNameAlt { get; }
    public GarmentKind Kind { get; }

    public string StyleName => string.IsNullOrWhiteSpace(StyleNameAlt) ? StyleNameEn : Localizer.Instance.Pick(StyleNameEn, StyleNameAlt);
    public string ColorName => Localizer.Instance.Pick(ColorNameEn, string.IsNullOrEmpty(ColorNameAlt) ? ColorNameEn : ColorNameAlt);
    public string Label => string.Join(" / ", new[] { Size, ColorName }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string FullName => $"{StyleName} ({Label})";
    public bool InStock => Stock > 0;
    public double Fill => Math.Min(100d, (double)Stock * 100d / 30);
}

public sealed class CartLine : ObservableObject
{
    decimal _qty;

    public required VariantItem Variant { get; init; }

    /// <summary>Whole pieces, or a fraction (kg, m, l) when the product allows it.</summary>
    public decimal Qty
    {
        get => _qty;
        set
        {
            var v = Variant.AllowDecimal ? Math.Round(value, 3) : Math.Round(value, 0);
            if (Set(ref _qty, Math.Max(Variant.AllowDecimal ? 0.001m : 1m, v))) { Raise(nameof(Total)); Raise(nameof(QtyText)); }
        }
    }

    public bool CanEditQty => Variant.AllowDecimal;
    public Action? Edited { get; set; }
    public string QtyText
    {
        get => _qty.ToString("0.###", CultureInfo.InvariantCulture);
        set
        {
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d > 0 && d <= Variant.Stock) { Qty = d; Edited?.Invoke(); }
            else Raise(nameof(QtyText));
        }
    }

    public string Name => Variant.StyleName;
    public string Size => Variant.Size;
    public string ColorName => Variant.ColorName;
    public decimal Price => Variant.Price;
    public decimal Total => Price * Qty;
}

public sealed class CustomerItem : LocalizedObject
{
    public CustomerItem(CustomerRow r)
    {
        Id = r.Id; Name = r.Name; Phone = r.Phone ?? ""; TotalPurchases = r.TotalPurchases; Outstanding = r.Balance; Email = r.Email ?? "";
        CreditEnabled = r.CreditEnabled; CreditLimit = r.CreditLimit;
    }

    public long Id { get; }
    public string Name { get; }
    public string Phone { get; }
    public decimal TotalPurchases { get; }
    public decimal Outstanding { get; }
    public string Email { get; }
    public bool CreditEnabled { get; }
    public decimal CreditLimit { get; }
    public decimal CreditLeft => Math.Max(0, CreditLimit - Outstanding);
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
    public string Status => Localizer.Instance[CreditEnabled ? "Cust_CreditOn" : "Status_Active"];
    public BadgeKind StatusKind => CreditEnabled ? BadgeKind.Info : BadgeKind.Success;
    public bool HasOutstanding => Outstanding > 0;
    public override string ToString() => Name;
}

public sealed class InvoiceItem : LocalizedObject
{
    public InvoiceItem(InvoiceRow r)
    {
        Id = r.Id; Number = r.Number; Date = DateTimeOffset.Parse(r.CreatedAt, CultureInfo.InvariantCulture); CustomerName = r.Customer;
        Cashier = r.Cashier; Items = r.Units; PaymentKey = r.PaymentKey; Total = r.Total; StatusKey = r.StatusKey;
        _paymentNameEn=r.PaymentNameEn;_paymentNameAr=r.PaymentNameAr;
    }

    public long Id { get; }
    public string Number { get; }
    public DateTimeOffset Date { get; }
    public string? CustomerName { get; }
    public string Cashier { get; }
    public int Items { get; }
    public string PaymentKey { get; }
    readonly string? _paymentNameEn,_paymentNameAr;
    public decimal Total { get; }
    public string StatusKey { get; }

    public string Customer => CustomerName ?? Localizer.Instance["Inv_WalkIn"];
    public string DateText => Date.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    public string Payment => _paymentNameEn is null?Localizer.Instance["Pay_" + PaymentKey]:Localizer.Instance.Pick(_paymentNameEn,_paymentNameAr??_paymentNameEn);
    public string Status => Localizer.Instance["Inv_" + StatusKey];
    public BadgeKind StatusKind => StatusKey switch { "Paid" => BadgeKind.Success, "Partial" => BadgeKind.Warning, _ => BadgeKind.Danger };
}

public sealed class MovementItem : LocalizedObject
{
    public MovementItem(MovementRow r)
    {
        No = (int)r.Id; Date = DateTimeOffset.Parse(r.CreatedAt, CultureInfo.InvariantCulture); TypeKey = r.TypeKey; NameEn = r.NameEn; NameAlt = r.NameAlt;
        Size = r.Size; ColorEn = r.ColorEn; ColorAlt = r.ColorAlt; Qty = r.Qty; Reference = r.Reference;
    }

    public int No { get; }
    public DateTimeOffset Date { get; }
    public string TypeKey { get; }
    public string NameEn { get; }
    public string? NameAlt { get; }
    public string? Size { get; }
    public string? ColorEn { get; }
    public string? ColorAlt { get; }
    public int Qty { get; }
    public string Reference { get; }

    public string DateText => Date.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    public string Type => Localizer.Instance["Mov_" + TypeKey];
    public string Product
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(NameAlt) ? NameEn : Localizer.Instance.Pick(NameEn, NameAlt);
            var color = Localizer.Instance.Pick(ColorEn ?? "", string.IsNullOrEmpty(ColorAlt) ? ColorEn ?? "" : ColorAlt);
            var v = string.Join(", ", new[] { Size, color }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return v.Length > 0 ? $"{name} ({v})" : name;
        }
    }

    public BadgeKind TypeKind => TypeKey switch { "Sale" => BadgeKind.Danger, "Purchase" => BadgeKind.Success, "Return" => BadgeKind.Info, _ => BadgeKind.Warning };
    public string QtyText => Qty > 0 ? $"+{Qty}" : Qty.ToString(CultureInfo.InvariantCulture);
    public bool IsIncrease => Qty > 0;
}

public sealed class ExpenseItem : LocalizedObject
{
    public ExpenseItem(ExpenseRow r)
    {
        Date = DateTime.Parse(r.Date, CultureInfo.InvariantCulture); CategoryKey = r.Category; Description = r.Description ?? ""; Amount = r.Amount;
        FromRegister = r.FromRegister; By = r.RecordedBy;
    }

    public DateTime Date { get; }
    public string CategoryKey { get; }
    public string Description { get; }
    public decimal Amount { get; }
    public bool FromRegister { get; }
    public string By { get; }

    public string DateText => Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    public string Category => Localizer.Instance["Exp_" + CategoryKey] is var c && c != "Exp_" + CategoryKey ? c : CategoryKey;
    public string PaidBy => Localizer.Instance[FromRegister ? "Exp_Register" : "Exp_External"];
}

public sealed class PaymentLineItem : LocalizedObject
{
    public required DateTimeOffset Date { get; init; }
    public required string Number { get; init; }
    public required string Method { get; init; }
    public required decimal Amount { get; init; }

    public string DateText => Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    public string MethodText => Localizer.Instance["Pay_" + char.ToUpperInvariant(Method[0]) + Method[1..]];
}

public sealed class TopProductRow : LocalizedObject
{
    public required int No { get; init; }
    public required string NameEn { get; init; }
    public string? NameAlt { get; init; }
    public required int Qty { get; init; }
    public required decimal Total { get; init; }

    public string Name => string.IsNullOrWhiteSpace(NameAlt) ? NameEn : Localizer.Instance.Pick(NameEn, NameAlt);
}

public sealed class CategoryShare : LocalizedObject
{
    public required string Key { get; init; }
    public required double Percent { get; init; }

    public string Name => CategoryNames.Label(Key);
    public string PercentText => $"{Percent:0}%";
}

public sealed class ShiftHistoryItem : LocalizedObject
{
    public ShiftHistoryItem(ShiftHistoryRow r) { Row = r; }

    public ShiftHistoryRow Row { get; }
    public string Date => Row.BusinessDate;
    public string Cashier => Row.Cashier;
    public string Opened => DateTimeOffset.Parse(Row.OpenedAt, CultureInfo.InvariantCulture).ToString("HH:mm", CultureInfo.InvariantCulture);
    public string Closed => Row.ClosedAt is null ? "—" : DateTimeOffset.Parse(Row.ClosedAt, CultureInfo.InvariantCulture).ToString("HH:mm", CultureInfo.InvariantCulture);
    public decimal Float => Row.OpeningFloat;
    public string Counted => Row.Counted is { } c ? c.ToString("N2", CultureInfo.InvariantCulture) : "—";
    public string Expected => Row.Expected is { } e ? e.ToString("N2", CultureInfo.InvariantCulture) : "—";
    public string Variance => Row.Variance is { } v ? v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) : "—";
    public bool HasVariance => Row.Variance is { } v && v != 0;
    public string Reason => Row.VarianceReason ?? "";
    public string Status => Localizer.Instance[Row.IsOpen ? "Shift_StatusOpen" : "Shift_StatusClosed"];
    public BadgeKind StatusKind => Row.IsOpen ? BadgeKind.Success : BadgeKind.Neutral;
}

public sealed class NotificationItem : LocalizedObject
{
    public required string TitleKey { get; init; }
    public required string DetailKey { get; init; }
    public object[] Args { get; init; } = [];
    public required BadgeKind Kind { get; init; }
    public required string Glyph { get; init; }

    public string Title => Localizer.Instance[TitleKey];
    public string Detail => string.Format(Localizer.Instance[DetailKey], Args);
}

/// <summary>A localized choice for ComboBoxes and chips (templates bind to Label).</summary>
/// <summary>A choice in a combo box. The label is a translated key, or computed (shop-defined names such as categories).</summary>
public sealed class Option(string key, Func<string>? label = null) : LocalizedObject
{
    public string Key { get; } = key;

    public string Label => label?.Invoke() ?? Localizer.Instance[Key];

    public override string ToString() => Label;
}

public sealed record ColorOption(string Hex, string NameEn, string NameAr)
{
    public string Name => Localizer.Instance.Pick(NameEn, NameAr);
}

/// <summary>Size and color choices offered by the product editor.</summary>
public static class GarmentOptions
{
    public static readonly string[] Sizes = ["XS", "S", "M", "L", "XL", "XXL", "28", "30", "32", "34", "36", "38", "One size"];

    public static readonly ColorOption[] Palette =
    [
        new("#1F2937", "Black", "أسود"), new("#F3F4F6", "White", "أبيض"), new("#1E3A5F", "Navy", "كحلي"), new("#3B6EA8", "Blue", "أزرق"),
        new("#A9C7E8", "Light Blue", "أزرق فاتح"), new("#9CA3AF", "Gray", "رمادي"), new("#D6B98C", "Beige", "بيج"), new("#F1E4CF", "Cream", "كريمي"),
        new("#7A8450", "Olive", "زيتي"), new("#C79A63", "Camel", "جملي"), new("#4F7CAC", "Denim", "جينز"), new("#B4443C", "Red", "أحمر"),
    ];

}
