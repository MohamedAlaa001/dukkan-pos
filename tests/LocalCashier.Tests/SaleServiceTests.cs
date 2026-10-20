using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LocalCashier.Tests;

public sealed class PosFixture : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lc-" + Guid.NewGuid().ToString("N"));
    public PosDatabase Db { get; }
    public IClock Clock { get; } = new SystemClock();
    public UserService Users { get; }
    public SettingsService Settings { get; }
    public CatalogService Catalog { get; }
    public CustomerService Customers { get; }
    public ShiftService Shifts { get; }
    public InventoryService Inventory { get; }
    public SaleService Sales { get; }
    public long Owner { get; }
    public long Cashier { get; }
    public long Shift { get; }
    public long Widget { get; }   // price 10.00, no tax, cost 4.00, stock 10

    public PosFixture(bool configureTax = true)
    {
        Directory.CreateDirectory(_dir);
        Db = new PosDatabase(Path.Combine(_dir, "pos.db"));
        Users = new UserService(Db, Clock);
        Settings = new SettingsService(Db, Clock);
        Catalog = new CatalogService(Db, Clock);
        Customers = new CustomerService(Db, Clock);
        Shifts = new ShiftService(Db, Clock);
        Inventory = new InventoryService(Db, Clock);
        Sales = new SaleService(Db, Clock);

        Owner = Users.Create("owner", "Owner", Role.Owner, "owner-pass");
        Cashier = Users.Create("cash", "Cashier", Role.Cashier, "cash-pass");
        if (configureTax) Settings.ConfigureTax(Owner, pricesIncludeTax: false);
        Widget = Catalog.AddProduct(Owner, "W1", "00123", "Widget", "ودجت", 10m);
        Inventory.Receive(Owner, Widget, 10, 4m);
        Shift = Shifts.Open(Cashier, 500m);
    }

    public PostSaleRequest Sale(decimal qty, decimal cash, Guid? op = null, Discount? orderDiscount = null,
        long? customer = null, long? approver = null, params PaymentRequest[] extra) =>
        new(op ?? Guid.NewGuid(), Shift, Cashier, customer, [new SaleLineRequest(Widget, qty)], orderDiscount,
            [.. (cash > 0 ? [new PaymentRequest(PaymentMethod.Cash, cash)] : Array.Empty<PaymentRequest>()), .. extra], approver);

    public long Count(string table)
    {
        using var c = Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}

public class SaleServiceTests
{
    [Fact]
    public void Cash_sale_posts_all_effects_and_change()
    {
        using var f = new PosFixture();
        var res = f.Sales.Post(f.Sale(3, cash: 50m));
        Assert.Equal(30m, res.Total);
        Assert.Equal(20m, res.Change);
        Assert.Equal(7m, f.Catalog.OnHand(f.Widget));
        Assert.Equal(530m, f.Shifts.ExpectedCash(f.Shift));   // change is not a drawer movement
        Assert.Equal(1, f.Count("print_jobs"));
    }

    [Fact]
    public void Same_operation_id_posts_once()
    {
        using var f = new PosFixture();
        var req = f.Sale(1, 10m);
        var a = f.Sales.Post(req);
        var b = f.Sales.Post(req);
        Assert.True(b.AlreadyPosted);
        Assert.Equal(a.Number, b.Number);
        Assert.Equal(1, f.Count("sales"));
        Assert.Equal(9m, f.Catalog.OnHand(f.Widget));
    }

    [Theory]
    [InlineData("after_sale_insert")]
    [InlineData("after_stock")]
    [InlineData("after_money")]
    public void Crash_mid_posting_leaves_no_partial_effects(string step)
    {
        using var f = new PosFixture();
        f.Sales.FaultInjector = s => { if (s == step) throw new IOException("simulated crash"); };
        Assert.Throws<IOException>(() => f.Sales.Post(f.Sale(2, 20m)));
        Assert.Equal(0, f.Count("sales"));
        Assert.Equal(0, f.Count("sale_lines"));
        Assert.Equal(0, f.Count("payments"));
        Assert.Equal(0, f.Count("print_jobs"));
        Assert.Equal(10m, f.Catalog.OnHand(f.Widget));
        Assert.Equal(500m, f.Shifts.ExpectedCash(f.Shift));
    }

    [Fact]
    public void Negative_stock_is_blocked()
    {
        using var f = new PosFixture();
        var ex = Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(11, 200m)));
        Assert.Equal(PosError.InsufficientStock, ex.Code);
        Assert.Equal(0, f.Count("sales"));
    }

    [Fact]
    public void Sales_are_blocked_until_tax_is_configured()
    {
        using var f = new PosFixture(configureTax: false);
        Assert.Equal(PosError.TaxNotConfigured, Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(1, 10m))).Code);
    }

    [Fact]
    public void Cashier_discount_needs_owner_approval()
    {
        using var f = new PosFixture();
        var denied = Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(1, 10m, orderDiscount: Discount.Fixed(1m))));
        Assert.Equal(PosError.ApprovalRequired, denied.Code);

        var cashierAsApprover = Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(1, 10m, orderDiscount: Discount.Fixed(1m), approver: f.Cashier)));
        Assert.Equal(PosError.PermissionDenied, cashierAsApprover.Code);

        var ok = f.Sales.Post(f.Sale(1, 10m, orderDiscount: Discount.Fixed(1m), approver: f.Owner));
        Assert.Equal(9m, ok.Total);
    }

    [Fact]
    public void Partial_payment_requires_credit_customer_within_limit()
    {
        using var f = new PosFixture();
        var noCustomer = Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(3, 10m)));
        Assert.Equal(PosError.CreditNotAllowed, noCustomer.Code);

        var cash = f.Customers.Add(f.Cashier, "Walk-in regular");
        Assert.Equal(PosError.CreditNotAllowed, Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(3, 10m, customer: cash))).Code);

        var credit = f.Customers.Add(f.Owner, "Credit customer", creditEnabled: true, creditLimit: 25m);
        Assert.Equal(PosError.CreditLimitExceeded, Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(5, 10m, customer: credit))).Code);

        var res = f.Sales.Post(f.Sale(3, 10m, customer: credit));   // 30 total, 10 paid, 20 on account
        Assert.Equal(20m, res.Debt);
        Assert.Equal(20m, f.Customers.Balance(credit));
        Assert.Equal(510m, f.Shifts.ExpectedCash(f.Shift));
    }

    [Fact]
    public void Electronic_payment_does_not_touch_drawer_and_cannot_overpay()
    {
        using var f = new PosFixture();
        var res = f.Sales.Post(f.Sale(2, cash: 0, extra: new PaymentRequest(PaymentMethod.Card, 20m)));
        Assert.Equal(0m, res.Change);
        Assert.Equal(500m, f.Shifts.ExpectedCash(f.Shift));

        var over = Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(1, cash: 0, extra: new PaymentRequest(PaymentMethod.Card, 15m))));
        Assert.Equal(PosError.PaymentMismatch, over.Code);
    }

    [Fact]
    public void Barcode_lookup_preserves_leading_zeros_and_unknown_is_null()
    {
        using var f = new PosFixture();
        Assert.Equal("Widget", f.Catalog.FindByBarcode("00123")?.Name);
        Assert.Null(f.Catalog.FindByBarcode("123"));
    }

    [Fact]
    public void Only_one_shift_can_be_open()
    {
        using var f = new PosFixture();
        Assert.Equal(PosError.ShiftAlreadyOpen, Assert.Throws<PosException>(() => f.Shifts.Open(f.Owner, 0m)).Code);
    }

    [Fact]
    public void Cashier_cannot_receive_stock()
    {
        using var f = new PosFixture();
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => f.Inventory.Receive(f.Cashier, f.Widget, 1, 1m)).Code);
    }

    [Fact]
    public void Receipt_at_new_cost_changes_average_but_not_sale_cogs()
    {
        using var f = new PosFixture();
        f.Sales.Post(f.Sale(1, 10m));   // snapshot cost 4.00
        f.Inventory.Receive(f.Owner, f.Widget, 9, 10m);   // 9 on hand @4 + 9 @10 -> 7.00

        using var c = f.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT unit_cost_x10000, (SELECT avg_cost_x10000 FROM products WHERE id=product_id) FROM sale_lines";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(40000, r.GetInt64(0));
        Assert.Equal(70000, r.GetInt64(1));
    }

    [Fact]
    public void Register_paid_delivery_books_one_cash_out()
    {
        using var f = new PosFixture();
        f.Inventory.Receive(f.Owner, f.Widget, 5, 2m, paidFromShiftId: f.Shift);
        Assert.Equal(490m, f.Shifts.ExpectedCash(f.Shift));
    }
}

public class AuthTests
{
    [Fact]
    public void Authentication_checks_password_and_hash_is_not_plaintext()
    {
        using var f = new PosFixture();
        Assert.NotNull(f.Users.Authenticate("owner", "owner-pass"));
        Assert.Null(f.Users.Authenticate("owner", "wrong"));
        Assert.StartsWith("pbkdf2-sha256$", PasswordHasher.Hash("x"));
    }
}

public class ReceiptTests
{
    [Fact]
    public void Receipt_is_built_from_posted_snapshot_even_after_price_changes()
    {
        using var f = new PosFixture();
        var res = f.Sales.Post(f.Sale(2, 25m));
        var r = f.Sales.GetReceipt(res.SaleId);
        Assert.Equal(res.Number, r.Number);
        Assert.Equal("Widget", r.Lines[0].Name);
        Assert.Equal(20m, r.Total);
        Assert.Equal(5m, r.Change);
        Assert.Equal("Cashier", r.Cashier);
    }
}
