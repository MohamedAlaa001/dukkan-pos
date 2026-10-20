#if DEBUG
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

/// <summary>
/// Creates a realistic demo shop through the same services the screens use (so it exercises real rules). Intended for trying the
/// application and for screenshots; the owner chooses it explicitly in setup and can also start with an empty shop.
/// </summary>
public static class DemoSeeder
{
    public const string OwnerUser = "owner";
    public const string OwnerPassword = "owner-demo-1";
    public const string CashierUser = "mona";
    public const string CashierPassword = "cashier-demo-1";

    static readonly (string En, string Ar, string Cat, string Kind, decimal Price, decimal Cost, string[] Colors, string[] Sizes)[] Styles =
    [
        ("T-Shirt", "تيشيرت", "TShirts", "TShirt", 250, 150, ["Black", "White", "Olive"], ["S", "M", "L", "XL"]),
        ("Polo Shirt", "قميص بولو", "TShirts", "Polo", 300, 180, ["Cream", "Navy"], ["M", "L", "XL"]),
        ("Jeans", "جينز", "Pants", "Jeans", 650, 400, ["Blue", "Black"], ["30", "32", "34", "36"]),
        ("Chinos", "بنطلون شينو", "Pants", "Chinos", 550, 330, ["Olive", "Beige"], ["30", "32", "34"]),
        ("Shorts", "شورت", "Pants", "Shorts", 400, 230, ["Beige", "Gray"], ["M", "L", "XL"]),
        ("Jacket", "جاكيت", "Jackets", "Jacket", 1200, 780, ["Black", "Denim"], ["M", "L", "XL"]),
        ("Hoodie", "هودي", "Hoodies", "Hoodie", 800, 480, ["Gray", "Navy"], ["M", "L", "XL"]),
        ("Shirt", "قميص", "Shirts", "Shirt", 450, 270, ["White", "Light Blue"], ["M", "L", "XL"]),
        ("Sweater", "سويتر", "Others", "Sweater", 700, 420, ["Camel", "Gray"], ["M", "L"]),
        ("Cap", "كاب", "Others", "Cap", 200, 90, ["Black", "Navy"], ["One size"]),
    ];

    static readonly Dictionary<string, (string Ar, string Hex)> Colors = new()
    {
        ["Black"] = ("أسود", "#1F2937"), ["White"] = ("أبيض", "#F3F4F6"), ["Olive"] = ("زيتي", "#7A8450"), ["Cream"] = ("كريمي", "#F1E4CF"),
        ["Navy"] = ("كحلي", "#1E3A5F"), ["Blue"] = ("أزرق", "#3B6EA8"), ["Beige"] = ("بيج", "#D6B98C"), ["Gray"] = ("رمادي", "#9CA3AF"),
        ["Denim"] = ("جينز", "#4F7CAC"), ["Light Blue"] = ("أزرق فاتح", "#A9C7E8"), ["Camel"] = ("جملي", "#C79A63"),
    };

    static readonly (string En, string Ar, string Phone, decimal Limit)[] Customers =
    [
        ("Ahmed Ali", "أحمد علي", "01012345678", 5000), ("Sara Mohamed", "سارة محمد", "01098765432", 0), ("Mostafa Hassan", "مصطفى حسن", "01055556666", 3000),
        ("Noha Ibrahim", "نها إبراهيم", "01011112222", 0), ("Mahmoud Adel", "محمود عادل", "01033334444", 2000), ("Yassin Sameh", "ياسين سامح", "01044445555", 0),
        ("Dina Ashraf", "دينا أشرف", "01066667777", 1500),
    ];

    /// <summary>Creates owner + cashier, tax settings, catalog with stock, customers and ~two weeks of trading history.</summary>
    public static void Seed(AppServices s, int days = 14, int seed = 7)
    {
        var owner = s.Users.Create(OwnerUser, "Ahmed Ali", Role.Owner, OwnerPassword);
        var cashier = s.Users.Create(CashierUser, "Mona Samir", Role.Cashier, CashierPassword);
        s.Settings.ConfigureTax(owner, pricesIncludeTax: true);
        s.Settings.SetValue(owner, "store_name", "Dukkan Demo Shop");
        s.Settings.SetValue(owner, "store_phone", "01012345678");
        s.Settings.SetValue(owner, "store_address", "12 Tahrir St, Cairo");
        s.Settings.SetValue(owner, "receipt_footer", "Exchange within 14 days with the receipt");

        var rng = new Random(seed);
        var start = s.Clock.Now.Date.AddDays(-days);
        s.Clock.Set(new DateTimeOffset(start.AddHours(9), DateTimeOffset.Now.Offset));

        // catalog + opening deliveries
        var variantIds = new List<long>();
        foreach (var st in Styles)
        {
            var input = new StyleInput(st.En, st.Ar, st.Cat, st.Kind, st.Price, st.Cost, 0m, 10);
            var variants = st.Colors.SelectMany(c => st.Sizes.Select(sz => new VariantInput(sz, c, Colors[c].Ar, Colors[c].Hex))).ToList();
            var id = s.Styles.CreateStyle(owner, input, variants);
            foreach (var v in s.Styles.Variants(id))
            {
                s.Inventory.Receive(owner, v.ProductId, rng.Next(6, 22), st.Cost, "Cairo Textile Co", "PO-" + (100 + id));
                variantIds.Add(v.ProductId);
            }
        }

        var customerIds = new List<long>();
        foreach (var c in Customers)
            customerIds.Add(s.Customers.Add(owner, c.En, c.Phone, c.Limit > 0, c.Limit, $"{c.En.Split(' ')[0].ToLowerInvariant()}@example.com"));

        // trading days
        for (var d = 0; d <= days; d++)
        {
            var day = start.AddDays(d);
            s.Clock.Set(new DateTimeOffset(day.AddHours(9), DateTimeOffset.Now.Offset));
            var shift = s.Shifts.Open(cashier, 500m);
            var sales = rng.Next(5, 11);
            for (var i = 0; i < sales; i++)
            {
                s.Clock.Set(new DateTimeOffset(day.AddHours(10).AddMinutes(i * 47 + rng.Next(0, 30)), DateTimeOffset.Now.Offset));
                var lines = new List<SaleLineRequest>();
                var picks = rng.Next(1, 4);
                for (var p = 0; p < picks; p++)
                {
                    var pid = variantIds[rng.Next(variantIds.Count)];
                    if (lines.Any(l => l.ProductId == pid) || s.Catalog.OnHand(pid) < 2) continue;
                    lines.Add(new SaleLineRequest(pid, rng.Next(1, 3)));
                }

                if (lines.Count == 0) continue;
                var total = lines.Sum(l => LookupPrice(s, l.ProductId) * l.Quantity);
                var roll = rng.Next(100);
                var payments = new List<PaymentRequest>();
                long? customer = null;
                if (roll < 55) payments.Add(new PaymentRequest(PaymentMethod.Cash, Math.Ceiling(total / 50m) * 50m));
                else if (roll < 75) payments.Add(new PaymentRequest(PaymentMethod.Card, total));
                else if (roll < 90) payments.Add(new PaymentRequest(PaymentMethod.Transfer, total));
                else
                {
                    var ci = rng.Next(customerIds.Count);
                    if (Customers[ci].Limit > 0)
                    {
                        customer = customerIds[ci];
                        payments.Add(new PaymentRequest(PaymentMethod.Cash, Math.Floor(total * 0.4m)));
                    }
                    else payments.Add(new PaymentRequest(PaymentMethod.Cash, Math.Ceiling(total / 50m) * 50m));
                }

                try { s.Sales.Post(new PostSaleRequest(Guid.NewGuid(), shift, cashier, customer, lines, null, payments)); }
                catch (PosException) { /* e.g. credit limit reached: skip this demo sale */ }
            }

            if (d == days - 2) SeedInstallmentSale(s, shift, cashier, customerIds[0], variantIds, day);
            s.Clock.Set(new DateTimeOffset(day.AddHours(21), DateTimeOffset.Now.Offset));
            if (d % 4 == 1) s.Cash.RecordExpense(owner, day.ToString("yyyy-MM-dd"), d % 8 == 1 ? "Utilities" : "Supplies", d % 8 == 1 ? 650m : 240m, d % 8 == 1 ? "Electricity" : "Hangers and bags", true, shift);
            var expected = s.Shifts.ExpectedCash(shift);
            s.Cash.CloseShift(cashier, shift, expected, null);
        }

        SeedSuppliers(s, owner, variantIds, start.AddDays(days));
        s.Clock.Set(null);
    }

    /// <summary>One customer bought on installments a couple of days ago; the first installment is already late so the overdue views have data.</summary>
    static void SeedInstallmentSale(AppServices s, long shift, long cashier, long customer, List<long> variantIds, DateTime day)
    {
        var pid = variantIds.First(v => s.Catalog.OnHand(v) >= 5);
        var price = LookupPrice(s, pid);
        var total = price * 3;
        var down = Math.Floor(total * 0.25m);
        var left = total - down;
        var plan = new List<InstallmentInput>();
        var parts = LocalCashier.Domain.InstallmentCalculator.EqualSplit(Money.ToCents(left), 3);
        for (var i = 0; i < 3; i++) plan.Add(new InstallmentInput(day.AddDays(-1 + 30 * i).ToString("yyyy-MM-dd"), Money.FromCents(parts[i])));
        try
        {
            s.Sales.Post(new PostSaleRequest(Guid.NewGuid(), shift, cashier, customer, [new SaleLineRequest(pid, 3)], null,
                [new PaymentRequest(PaymentMethod.Cash, down)], null, new CreditArrangement(null, plan)));
        }
        catch (PosException) { /* demo data only */ }
    }

    /// <summary>Three suppliers: one fully paid, one part-paid with a late invoice, one with only an opening balance.</summary>
    static void SeedSuppliers(AppServices s, long owner, List<long> variantIds, DateTime today)
    {
        var nile = s.Suppliers.Create(owner, "Nile Textiles", "01001112233", "Mahalla El-Kubra", "Main fabric supplier");
        var cairo = s.Suppliers.Create(owner, "Cairo Garments Co", "01114445566", "Shubra, Cairo", null);
        var delta = s.Suppliers.Create(owner, "Delta Accessories", "01227778899", null, "Caps and belts");
        string D(int offset) => today.AddDays(offset).ToString("yyyy-MM-dd");
        var items = variantIds.Take(6).ToList();
        s.Suppliers.PostPurchase(new PostPurchaseRequest(Guid.NewGuid(), owner, nile, "NT-2210", D(-20), D(-5),
            [new PurchaseLine(items[0], 40, 120m), new PurchaseLine(items[1], 30, 150m)], 4000m, PaymentMethod.Transfer, null, "First delivery"));
        s.Suppliers.PostPurchase(new PostPurchaseRequest(Guid.NewGuid(), owner, nile, "NT-2288", D(-6), D(12),
            [new PurchaseLine(items[2], 50, 95m)], 0m, PaymentMethod.Transfer));
        s.Suppliers.PostPurchase(new PostPurchaseRequest(Guid.NewGuid(), owner, cairo, "CG-77", D(-9), D(21),
            [new PurchaseLine(items[3], 25, 210m)], 5250m, PaymentMethod.Card));
        s.Suppliers.AddOpeningBalance(Guid.NewGuid(), owner, delta, 1800m, D(7), "Balance carried from the paper ledger", D(-30));
    }

    static decimal LookupPrice(AppServices s, long productId)
    {
        using var c = s.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT price_cents FROM products WHERE id=@i";
        cmd.Parameters.AddWithValue("@i", productId);
        return Convert.ToInt64(cmd.ExecuteScalar()) / 100m;
    }
}

#endif
