using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using Xunit;

namespace LocalCashier.Tests;

public class SupplierTests
{
    [Fact]
    public void Reversing_opening_balance_preserves_purchase_totals_and_clears_opening()
    {
        using var x = new Ctx();
        x.S.PostPurchase(x.Purchase(5, 40m, 0m));
        var opening = x.S.AddOpeningBalance(Guid.NewGuid(), x.F.Owner, x.Supplier, 25m);
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        x.S.ReversePurchase(x.F.Owner, opening, "Wrong opening balance");
        var supplier = x.S.Get(x.Supplier)!;
        Assert.Equal(200m, supplier.TotalPurchases);
        Assert.Equal(0m, supplier.Opening);
        Assert.Equal(200m, supplier.Balance);
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
    }

    sealed class Ctx : IDisposable
    {
        public PosFixture F { get; } = new();
        public SupplierService S { get; }
        public long Supplier { get; }
        public Ctx()
        {
            S = new SupplierService(F.Db, F.Clock);
            Supplier = S.Create(F.Owner, "Nile Textiles", "0100", "Cairo", null);
        }
        public PostPurchaseRequest Purchase(decimal qty, decimal cost, decimal paid, PaymentMethod m = PaymentMethod.Card, Guid? op = null, string? due = null, string date = "2026-01-10") =>
            new(op ?? Guid.NewGuid(), F.Owner, Supplier, "INV-1", date, due, [new PurchaseLine(F.Widget, qty, cost)], paid, m, F.Shift);
        public SupplierPayRequest Pay(decimal amount, PaymentMethod m = PaymentMethod.Card, Guid? op = null, IReadOnlyList<SupplierAllocation>? alloc = null) =>
            new(op ?? Guid.NewGuid(), F.Owner, Supplier, "2026-01-20", amount, m, null, null, F.Shift, alloc);
        public void Dispose() => F.Dispose();
    }

    [Fact]
    public void Purchase_20000_paid_5000_increases_stock_once_and_owes_15000()
    {
        using var x = new Ctx();
        var before = x.F.Catalog.OnHand(x.F.Widget);
        var res = x.S.PostPurchase(x.Purchase(2000, 10m, 5000m));
        Assert.Equal(20000m, res.Total);
        Assert.Equal(15000m, res.Outstanding);
        Assert.Equal(before + 2000, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(15000m, x.S.OutstandingBalance(x.Supplier));
        var inv = x.S.InvoiceList(x.Supplier).Single();
        Assert.Equal("partial", inv.Status);
    }

    [Fact]
    public void Later_payment_reduces_balance_without_stock_change()
    {
        using var x = new Ctx();
        x.S.PostPurchase(x.Purchase(2000, 10m, 5000m));
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        var invoices = x.F.Count("supplier_invoices");
        x.S.Pay(x.Pay(3000m));
        Assert.Equal(12000m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(invoices, x.F.Count("supplier_invoices"));
    }

    [Fact]
    public void Unpaid_purchase_owes_everything_and_fully_paid_owes_nothing()
    {
        using var x = new Ctx();
        x.S.PostPurchase(x.Purchase(100, 10m, 0m));
        Assert.Equal(1000m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal("unpaid", x.S.InvoiceList(x.Supplier).Single().Status);
        x.S.PostPurchase(x.Purchase(50, 10m, 500m));
        Assert.Equal(1000m, x.S.OutstandingBalance(x.Supplier));
        Assert.Contains(x.S.InvoiceList(x.Supplier), i => i.Status == "paid" && i.Outstanding == 0m);
    }

    [Fact]
    public void One_payment_can_be_allocated_across_invoices_oldest_first_or_manually()
    {
        using var x = new Ctx();
        var a = x.S.PostPurchase(x.Purchase(100, 10m, 0m, date: "2026-01-01"));
        var b = x.S.PostPurchase(x.Purchase(100, 10m, 0m, date: "2026-01-05"));
        var res = x.S.Pay(x.Pay(1500m));
        Assert.Equal([a.InvoiceId, b.InvoiceId], res.Allocations.Select(p => p.InvoiceId).ToArray());
        Assert.Equal([1000m, 500m], res.Allocations.Select(p => p.Amount).ToArray());

        var manual = x.S.Pay(x.Pay(300m, alloc: [new SupplierAllocation(b.InvoiceId, 300m)]));
        Assert.Single(manual.Allocations);
        Assert.Equal(200m, x.S.InvoiceList(x.Supplier).First(i => i.Id == b.InvoiceId).Outstanding);
        Assert.Equal(200m, x.S.OutstandingBalance(x.Supplier));
    }

    [Fact]
    public void Overpayment_and_bad_allocation_are_rejected_without_effects()
    {
        using var x = new Ctx();
        var a = x.S.PostPurchase(x.Purchase(100, 10m, 0m));
        var payments = x.F.Count("supplier_payments");
        var ledger = x.F.Count("supplier_ledger");
        Assert.Equal(PosError.PaymentMismatch, Assert.Throws<PosException>(() => x.S.Pay(x.Pay(1000.01m))).Code);
        Assert.Equal(PosError.PaymentMismatch, Assert.Throws<PosException>(() =>
            x.S.Pay(x.Pay(100m, alloc: [new SupplierAllocation(a.InvoiceId, 50m)]))).Code);   // not fully allocated
        Assert.Equal(payments, x.F.Count("supplier_payments"));
        Assert.Equal(ledger, x.F.Count("supplier_ledger"));
    }

    [Fact]
    public void Retries_do_not_duplicate_purchase_payment_or_stock()
    {
        using var x = new Ctx();
        var op = Guid.NewGuid();
        var first = x.S.PostPurchase(x.Purchase(100, 10m, 200m, op: op));
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        var again = x.S.PostPurchase(x.Purchase(100, 10m, 200m, op: op));
        Assert.True(again.AlreadyPosted);
        Assert.Equal(first.InvoiceId, again.InvoiceId);
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(1, x.F.Count("supplier_invoices"));
        Assert.Equal(1, x.F.Count("supplier_payments"));

        var pop = Guid.NewGuid();
        x.S.Pay(x.Pay(100m, op: pop));
        Assert.True(x.S.Pay(x.Pay(100m, op: pop)).AlreadyPosted);
        Assert.Equal(2, x.F.Count("supplier_payments"));
        Assert.Equal(700m, x.S.OutstandingBalance(x.Supplier));
    }

    [Fact]
    public void Cash_payment_moves_drawer_once_and_card_does_not()
    {
        using var x = new Ctx();
        x.S.PostPurchase(x.Purchase(100, 10m, 0m));
        var start = x.F.Shifts.ExpectedCash(x.F.Shift);
        var cash = x.S.Pay(x.Pay(200m, PaymentMethod.Cash));
        Assert.Equal(start - 200m, x.F.Shifts.ExpectedCash(x.F.Shift));
        x.S.Pay(x.Pay(100m, PaymentMethod.Card));
        x.S.Pay(x.Pay(100m, PaymentMethod.Transfer));
        Assert.Equal(start - 200m, x.F.Shifts.ExpectedCash(x.F.Shift));

        // reversing the cash payment restores the drawer exactly once and the supplier balance
        x.S.ReversePayment(x.F.Owner, cash.PaymentId, "wrong supplier", x.F.Shift);
        Assert.Equal(start, x.F.Shifts.ExpectedCash(x.F.Shift));
        Assert.Equal(800m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal(PosError.Duplicate, Assert.Throws<PosException>(() => x.S.ReversePayment(x.F.Owner, cash.PaymentId, "again", x.F.Shift)).Code);
        Assert.Equal(800m, x.S.OutstandingBalance(x.Supplier));
    }

    [Fact]
    public void Cash_payment_needs_a_shift_and_enough_drawer_cash()
    {
        using var x = new Ctx();
        x.S.PostPurchase(x.Purchase(1000, 10m, 0m));
        var req = x.Pay(900m, PaymentMethod.Cash) with { ShiftId = null };
        Assert.Equal(PosError.ShiftNotOpen, Assert.Throws<PosException>(() => x.S.Pay(req)).Code);
        Assert.Equal(PosError.PaymentMismatch, Assert.Throws<PosException>(() => x.S.Pay(x.Pay(900m, PaymentMethod.Cash))).Code);   // drawer holds 500
        Assert.Equal(0, x.F.Count("supplier_payments"));
    }

    [Fact]
    public void Opening_balance_creates_no_stock_and_no_purchase()
    {
        using var x = new Ctx();
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        var receipts = x.F.Count("stock_receipts");
        x.S.AddOpeningBalance(Guid.NewGuid(), x.F.Owner, x.Supplier, 7500m, "2026-03-01", "carried over");
        Assert.Equal(7500m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(receipts, x.F.Count("stock_receipts"));
        Assert.Equal(0, x.F.Count("supplier_invoice_lines"));
        Assert.Equal("opening", x.S.Statement(x.Supplier).Single().Type);
        x.S.Pay(x.Pay(2500m));
        Assert.Equal(5000m, x.S.OutstandingBalance(x.Supplier));
    }

    [Fact]
    public void Failure_during_posting_leaves_no_partial_effects()
    {
        using var x = new Ctx();
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        foreach (var step in new[] { "after_invoice_insert", "after_stock", "after_payment" })
        {
            x.S.FaultInjector = s => { if (s == step) throw new IOException("crash"); };
            Assert.Throws<IOException>(() => x.S.PostPurchase(x.Purchase(100, 10m, 300m)));
        }
        x.S.FaultInjector = null;
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(0, x.F.Count("supplier_invoices"));
        Assert.Equal(0, x.F.Count("supplier_payments"));
        Assert.Equal(0, x.F.Count("supplier_ledger"));
        Assert.Equal(0, x.F.Count("stock_receipts") - 1);   // only the fixture's initial receipt
    }

    [Fact]
    public void Only_the_owner_can_post_pay_open_or_reverse()
    {
        using var x = new Ctx();
        var inv = x.S.PostPurchase(x.Purchase(100, 10m, 0m));
        var pay = x.S.Pay(x.Pay(100m));
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.PostPurchase(x.Purchase(1, 1m, 0m) with { ActorId = x.F.Cashier })).Code);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.Pay(x.Pay(1m) with { ActorId = x.F.Cashier })).Code);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.AddOpeningBalance(Guid.NewGuid(), x.F.Cashier, x.Supplier, 10m)).Code);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.ReversePayment(x.F.Cashier, pay.PaymentId, "x", x.F.Shift)).Code);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.ReversePurchase(x.F.Cashier, inv.InvoiceId, "x")).Code);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.Create(x.F.Cashier, "Sneaky")).Code);
    }

    [Fact]
    public void Purchase_reversal_is_blocked_by_payments_and_by_sold_stock_then_succeeds()
    {
        using var x = new Ctx();
        var inv = x.S.PostPurchase(x.Purchase(100, 10m, 400m));
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.ReversePurchase(x.F.Owner, inv.InvoiceId, "oops")).Code);
        var pay = x.S.Payments(x.Supplier).Single();
        x.S.ReversePayment(x.F.Owner, pay.Id, "oops", x.F.Shift);
        // sell enough stock that the received goods are no longer all on hand
        x.F.Sales.Post(x.F.Sale(105, cash: 5000m));
        Assert.Equal(PosError.InsufficientStock, Assert.Throws<PosException>(() => x.S.ReversePurchase(x.F.Owner, inv.InvoiceId, "oops")).Code);
        Assert.True(x.F.Catalog.OnHand(x.F.Widget) >= 0);
    }

    [Fact]
    public void Purchase_reversal_removes_stock_and_debt()
    {
        using var x = new Ctx();
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        var inv = x.S.PostPurchase(x.Purchase(100, 10m, 0m));
        x.S.ReversePurchase(x.F.Owner, inv.InvoiceId, "entered twice");
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(0m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal("reversed", x.S.InvoiceList(x.Supplier).Single().Status);
        Assert.Equal(PosError.Duplicate, Assert.Throws<PosException>(() => x.S.ReversePurchase(x.F.Owner, inv.InvoiceId, "again")).Code);
    }

    [Fact]
    public void Overdue_is_separate_from_status_and_reports_filter()
    {
        using var x = new Ctx();
        x.S.PostPurchase(x.Purchase(100, 10m, 0m, due: "2020-01-01"));
        x.S.PostPurchase(x.Purchase(100, 10m, 0m, due: "2099-01-01"));
        var inv = x.S.InvoiceList(x.Supplier);
        Assert.Equal(1, inv.Count(i => i.Overdue));
        Assert.All(inv, i => Assert.Equal("unpaid", i.Status));
        Assert.Single(x.S.InvoiceList(x.Supplier, status: "overdue"));
        Assert.Equal(1000m, x.S.DueSummary().Overdue);
        Assert.Equal(2000m, x.S.DueSummary().Outstanding);
        Assert.Equal(1000m, x.S.Get(x.Supplier)!.Overdue);
    }

    [Fact]
    public void Suppliers_with_transactions_are_archived_and_data_survives_restart()
    {
        using var x = new Ctx();
        x.S.PostPurchase(x.Purchase(100, 10m, 400m));
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.SetActive(x.F.Owner, x.Supplier, false)).Code);
        x.S.Pay(x.Pay(600m));
        x.S.SetActive(x.F.Owner, x.Supplier, false);
        Assert.Empty(x.S.List());
        Assert.Single(x.S.List(null, true));

        var reopened = new SupplierService(new PosDatabase(x.F.Db.Path), x.F.Clock);
        var row = reopened.Get(x.Supplier)!;
        Assert.False(row.Active);
        Assert.Equal(0m, row.Balance);
        Assert.Equal(1000m, row.TotalPurchases);
        Assert.Equal(1000m, row.TotalPaid);
        Assert.Equal(3, reopened.Statement(x.Supplier).Count);   // purchase, initial payment, payment
        Assert.Equal(0m, reopened.Statement(x.Supplier).Last().Running);
    }

    [Fact]
    public void Purchases_and_payments_are_not_expenses()
    {
        using var x = new Ctx();
        x.S.PostPurchase(x.Purchase(100, 10m, 500m));
        x.S.Pay(x.Pay(500m));
        Assert.Equal(0, x.F.Count("expenses"));
        Assert.Equal(0m, new ReportService(x.F.Db).ExpensesTotal("2000-01-01", "2099-12-31"));
    }
}
