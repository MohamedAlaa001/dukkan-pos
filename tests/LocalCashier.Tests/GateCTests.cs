using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using Xunit;

namespace LocalCashier.Tests;

public class GateCFixture : IDisposable
{
    public PosFixture F { get; } = new();
    public ReturnService Returns { get; }
    public CollectionService Collections { get; }
    public CashService Cash { get; }
    public StockAdjustmentService Adjust { get; }
    public ReportService Reports { get; }

    public GateCFixture()
    {
        Returns = new ReturnService(F.Db, F.Clock);
        Collections = new CollectionService(F.Db, F.Clock);
        Cash = new CashService(F.Db, F.Clock);
        Adjust = new StockAdjustmentService(F.Db, F.Clock);
        Reports = new ReportService(F.Db);
    }

    public long FirstLineOf(long saleId)
    {
        using var c = F.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM sale_lines WHERE sale_id=@s ORDER BY line_no LIMIT 1";
        cmd.Parameters.AddWithValue("@s", saleId);
        return (long)cmd.ExecuteScalar()!;
    }

    public PostReturnRequest Return(long saleId, decimal qty, bool resalable = true, PaymentMethod? refund = PaymentMethod.Cash, long? approver = null, Guid? op = null) =>
        new(op ?? Guid.NewGuid(), saleId, F.Shift, approver is null ? F.Owner : F.Cashier, approver, "customer changed mind",
            [new ReturnLineRequest(FirstLineOf(saleId), qty, resalable)], refund);

    public void Dispose() => F.Dispose();
}

public class GateCTests
{
    // AT-05: invoice 300; collect 100; return goods worth 250 -> debt 200 to zero, refund 50
    [Fact]
    public void AT05_return_reduces_debt_first_then_refunds_collected_money()
    {
        using var g = new GateCFixture();
        var f = g.F;
        f.Inventory.Receive(f.Owner, f.Widget, 20, 4m);                       // 30 on hand
        var credit = f.Customers.Add(f.Owner, "Credit", creditEnabled: true, creditLimit: 1000m);
        var sale = f.Sales.Post(f.Sale(30, cash: 0, customer: credit));       // 300 on account
        Assert.Equal(300m, f.Customers.Balance(credit));

        g.Collections.Collect(Guid.NewGuid(), credit, 100m, PaymentMethod.Cash, f.Shift, f.Cashier);
        Assert.Equal(200m, f.Customers.Balance(credit));

        var ret = g.Returns.Post(g.Return(sale.SaleId, 25));                  // 250 worth
        Assert.Equal(250m, ret.Total);
        Assert.Equal(200m, ret.DebtReduction);
        Assert.Equal(50m, ret.Refund);
        Assert.Equal(0m, f.Customers.Balance(credit));
        Assert.Equal(0m, g.Collections.Outstanding(sale.SaleId));

        var s = g.Reports.Summary("0000-01-01", "9999-12-31");
        Assert.Equal(50m, s.NetSalesExTax);                                   // 300 - 250
    }

    // AT-06: repeated partial returns after a discounted sale never exceed the original
    [Fact]
    public void AT06_repeated_partial_returns_are_capped()
    {
        using var g = new GateCFixture();
        var f = g.F;
        var sale = f.Sales.Post(f.Sale(3, cash: 100m, orderDiscount: Discount.Fixed(0.01m), approver: f.Owner));   // 29.99
        decimal refunded = 0;
        for (var i = 0; i < 3; i++) refunded += g.Returns.Post(g.Return(sale.SaleId, 1)).Total;
        Assert.Equal(29.99m, refunded);
        var ex = Assert.Throws<PosException>(() => g.Returns.Post(g.Return(sale.SaleId, 1)));
        Assert.Equal(PosError.ReturnExceedsSold, ex.Code);
    }

    // AT-07: opening 10; receive 5; sell 3; resalable return 1; loss 2 -> sellable 11
    [Fact]
    public void AT07_stock_ledger_reconciles()
    {
        using var g = new GateCFixture();
        var f = g.F;
        f.Inventory.Receive(f.Owner, f.Widget, 5, 4m);
        var sale = f.Sales.Post(f.Sale(3, 30m));
        g.Returns.Post(g.Return(sale.SaleId, 1, resalable: true));            // on hand 13
        g.Adjust.ApplyCount(f.Owner, "damaged", [new CountItem(f.Widget, 11m)]);   // loss of 2
        Assert.Equal(11m, f.Catalog.OnHand(f.Widget));
        var s = g.Reports.Summary("0000-01-01", "9999-12-31");
        Assert.Equal(8m, s.StockLosses);                                       // 2 x 4.00
    }

    // AT-08: open 500; cash sales 1,000; collect 200; expense 100; refund 50 -> 1,550
    [Fact]
    public void AT08_expected_cash_formula()
    {
        using var g = new GateCFixture();
        var f = g.F;
        f.Inventory.Receive(f.Owner, f.Widget, 200, 4m);
        var credit = f.Customers.Add(f.Owner, "Credit", creditEnabled: true, creditLimit: 1000m);
        var cashSale = f.Sales.Post(f.Sale(100, 1000m));
        f.Sales.Post(f.Sale(20, cash: 0, customer: credit));                    // 200 on account
        g.Collections.Collect(Guid.NewGuid(), credit, 200m, PaymentMethod.Cash, f.Shift, f.Cashier);
        g.Cash.RecordExpense(f.Owner, "2026-10-19", "rent", 100m, null, fromRegister: true, f.Shift);
        g.Returns.Post(g.Return(cashSale.SaleId, 5));                          // 50 cash refund
        Assert.Equal(1550m, f.Shifts.ExpectedCash(f.Shift));
    }

    // AT-09 (average cost moves, history does not) is covered in SaleServiceTests; here: close shift blind + variance
    [Fact]
    public void Shift_close_requires_reason_for_variance_and_is_final()
    {
        using var g = new GateCFixture();
        var f = g.F;
        f.Sales.Post(f.Sale(1, 10m));                                          // expected 510
        var ex = Assert.Throws<PosException>(() => g.Cash.CloseShift(f.Cashier, f.Shift, 500m, null));
        Assert.Equal(PosError.InvalidDiscount, ex.Code);

        var res = g.Cash.CloseShift(f.Cashier, f.Shift, 500m, "short");
        Assert.Equal(-10m, res.Variance);
        Assert.Equal(510m, res.Expected);

        Assert.Equal(PosError.ShiftNotOpen, Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(1, 10m))).Code);
        var rep = g.Reports.Shift(f.Shift);
        Assert.Equal(-10m, rep.Variance);
        Assert.Equal(10m, rep.Movements["sale_cash"]);
    }

    // AT-10 subset: owner-only operations
    [Fact]
    public void Cashier_cannot_return_without_owner_or_do_owner_cash()
    {
        using var g = new GateCFixture();
        var f = g.F;
        var sale = f.Sales.Post(f.Sale(1, 10m));
        var denied = Assert.Throws<PosException>(() => g.Returns.Post(new PostReturnRequest(Guid.NewGuid(), sale.SaleId, f.Shift, f.Cashier, null, "x",
            [new ReturnLineRequest(g.FirstLineOf(sale.SaleId), 1, true)], PaymentMethod.Cash)));
        Assert.Equal(PosError.ApprovalRequired, denied.Code);
        Assert.Equal(1, f.Count("sales"));
        Assert.Equal(0, f.Count("returns"));

        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => g.Cash.RecordOwnerCash(f.Cashier, f.Shift, true, 5m, "x")).Code);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => g.Adjust.ApplyCount(f.Cashier, "x", [new CountItem(f.Widget, 1m)])).Code);

        var approved = g.Returns.Post(g.Return(sale.SaleId, 1, approver: f.Owner));
        Assert.Equal(10m, approved.Refund);
    }

    [Fact]
    public void Return_is_idempotent_and_atomic()
    {
        using var g = new GateCFixture();
        var f = g.F;
        var sale = f.Sales.Post(f.Sale(2, 20m));
        var req = g.Return(sale.SaleId, 1);
        g.Returns.FaultInjector = s => { if (s == "after_return_stock") throw new IOException("crash"); };
        Assert.Throws<IOException>(() => g.Returns.Post(req));
        Assert.Equal(0, f.Count("returns"));
        Assert.Equal(0, f.Count("return_lines"));
        Assert.Equal(8m, f.Catalog.OnHand(f.Widget));

        g.Returns.FaultInjector = null;
        var a = g.Returns.Post(req);
        var b = g.Returns.Post(req);
        Assert.True(b.AlreadyPosted);
        Assert.Equal(a.Number, b.Number);
        Assert.Equal(9m, f.Catalog.OnHand(f.Widget));
    }

    [Fact]
    public void Non_resalable_return_records_loss_without_restocking()
    {
        using var g = new GateCFixture();
        var f = g.F;
        var sale = f.Sales.Post(f.Sale(2, 20m));
        g.Returns.Post(g.Return(sale.SaleId, 1, resalable: false));
        Assert.Equal(8m, f.Catalog.OnHand(f.Widget));
        var s = g.Reports.Summary("0000-01-01", "9999-12-31");
        Assert.Equal(4m, s.StockLosses);
        Assert.Equal(4m, s.CostOfGoods);   // 8.00 sold cost less 4.00 reversed
    }

    [Fact]
    public void Collection_cannot_exceed_debt_and_custom_allocation_is_owner_only()
    {
        using var g = new GateCFixture();
        var f = g.F;
        var credit = f.Customers.Add(f.Owner, "Credit", creditEnabled: true, creditLimit: 1000m);
        var s1 = f.Sales.Post(f.Sale(2, cash: 0, customer: credit));   // 20
        var s2 = f.Sales.Post(f.Sale(3, cash: 0, customer: credit));   // 30

        Assert.Equal(PosError.PaymentMismatch,
            Assert.Throws<PosException>(() => g.Collections.Collect(Guid.NewGuid(), credit, 60m, PaymentMethod.Cash, f.Shift, f.Cashier)).Code);

        var oldestFirst = g.Collections.Collect(Guid.NewGuid(), credit, 25m, PaymentMethod.Cash, f.Shift, f.Cashier);
        Assert.Equal([20m, 5m], oldestFirst.Allocations.Select(a => a.Amount).ToArray());

        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() =>
            g.Collections.Collect(Guid.NewGuid(), credit, 5m, PaymentMethod.Cash, f.Shift, f.Cashier, [new AllocationRequest(s2.SaleId, 5m)])).Code);
        g.Collections.Collect(Guid.NewGuid(), credit, 5m, PaymentMethod.Cash, f.Shift, f.Owner, [new AllocationRequest(s2.SaleId, 5m)]);
        Assert.Equal(20m, f.Customers.Balance(credit));
    }

    // AT-14 (reporting side): business date vs timestamp date are distinguishable
    [Fact]
    public void Reports_can_group_by_business_date_or_timestamp()
    {
        using var g = new GateCFixture();
        var f = g.F;
        f.Sales.Post(f.Sale(1, 10m));
        using (var c = f.Db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE shifts SET business_date='2026-01-01'";   // shift opened yesterday, still trading past midnight
            cmd.ExecuteNonQuery();
            using var cmd2 = c.CreateCommand();
            cmd2.CommandText = "UPDATE sales SET business_date='2026-01-01'";
            cmd2.ExecuteNonQuery();
        }
        Assert.Equal(1, g.Reports.Summary("2026-01-01", "2026-01-01", byBusinessDate: true).SaleCount);
        Assert.Equal(0, g.Reports.Summary("2026-01-01", "2026-01-01", byBusinessDate: false).SaleCount);
    }

    [Fact]
    public void Statement_shows_running_balance_with_document_numbers_and_open_invoices()
    {
        using var g = new GateCFixture();
        var f = g.F;
        var credit = f.Customers.Add(f.Owner, "Credit", creditEnabled: true, creditLimit: 500m);
        var sale = f.Sales.Post(f.Sale(5, cash: 20m, customer: credit));            // 50 total, 20 paid, 30 owed
        g.Collections.Collect(Guid.NewGuid(), credit, 10m, PaymentMethod.Cash, f.Shift, f.Cashier);

        var st = f.Customers.Statement(credit);
        Assert.Equal([30m, 20m], st.Select(e => e.Running).ToArray());
        Assert.Equal(sale.Number, st[0].Document);
        Assert.StartsWith("C1-", st[1].Document);

        var inv = Assert.Single(f.Customers.OpenInvoices(credit));
        Assert.Equal(20m, inv.Outstanding);
        Assert.Equal(50m, inv.Total);

        var row = f.Customers.List().Single(c => c.Id == credit);
        Assert.Equal(20m, row.Balance);
        Assert.True(row.CreditEnabled);
    }

    [Fact]
    public void Only_owner_changes_credit_and_new_limit_applies()
    {
        using var g = new GateCFixture();
        var f = g.F;
        var cust = f.Customers.Add(f.Cashier, "Newcomer");
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => f.Customers.SetCredit(f.Cashier, cust, true, 100m)).Code);
        Assert.Equal(PosError.CreditNotAllowed, Assert.Throws<PosException>(() => f.Sales.Post(f.Sale(2, cash: 0, customer: cust))).Code);
        f.Customers.SetCredit(f.Owner, cust, true, 100m);
        Assert.Equal(20m, f.Sales.Post(f.Sale(2, cash: 0, customer: cust)).Debt);
    }

    [Fact]
    public void Stock_list_matches_ledger_and_count_posts_one_adjustment()
    {
        using var g = new GateCFixture();
        var f = g.F;
        var row = Assert.Single(f.Catalog.ListStock());
        Assert.Equal(10m, row.OnHand);
        Assert.Equal(4m, row.AvgCost);
        g.Adjust.ApplyCount(f.Owner, "annual count", [new CountItem(row.ProductId, 12m, GainUnitCost: 4m)]);
        Assert.Equal(12m, f.Catalog.ListStock().Single().OnHand);
        Assert.Equal(1, f.Count("stock_adjustments"));
        Assert.Equal(PosError.InvalidPrice, Assert.Throws<PosException>(() =>
            g.Adjust.ApplyCount(f.Owner, "no cost", [new CountItem(row.ProductId, 20m)])).Code);
        Assert.Equal(12m, f.Catalog.ListStock().Single().OnHand);
    }

    [Fact]
    public void Customer_balances_and_low_stock_reports()
    {
        using var g = new GateCFixture();
        var f = g.F;
        var credit = f.Customers.Add(f.Owner, "Credit", creditEnabled: true, creditLimit: 1000m);
        f.Sales.Post(f.Sale(2, cash: 0, customer: credit));
        Assert.Equal(20m, g.Reports.CustomerBalances().Single().Balance);
        Assert.Empty(g.Reports.LowStock());   // 8 on hand, threshold 0
    }
}
