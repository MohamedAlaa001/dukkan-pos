using LocalCashier.Domain;
using LocalCashier.Application;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class SupplierOrderReturnTests
{
    sealed class Ctx : IDisposable
    {
        public PosFixture F { get; } = new();
        public SupplierService S { get; }
        public long Supplier { get; }
        public Ctx() { S = new(F.Db, F.Clock); Supplier = S.Create(F.Owner, "Supplier"); }
        public PurchaseResult Buy(decimal qty = 10, decimal cost = 10, decimal paid = 0) => S.PostPurchase(new(
            Guid.NewGuid(), F.Owner, Supplier, null, "2026-01-10", null, [new(F.Widget, qty, cost)], paid, PaymentMethod.Transfer));
        public SupplierReturnRequest Return(long? invoice, decimal quantity, decimal cost = 10, Guid? op = null) => new(
            op ?? Guid.NewGuid(), F.Owner, Supplier, "2026-01-12", "damaged", "Check credit", invoice,
            [new(F.Widget, quantity, cost, invoice is long i ? S.PurchaseLines(i).Single().Id : null)]);
        public void Dispose() => F.Dispose();
    }

    [Fact]
    public void Orders_have_no_financial_effect_and_receipts_track_partial_complete_and_retry()
    {
        using var x = new Ctx();
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        var op = Guid.NewGuid();
        var id = x.S.CreateOrder(op, x.F.Owner, x.Supplier, [new(x.F.Widget, 10, 4.1256m)], "2026-01-09", "Order notes");
        Assert.Equal(id, x.S.CreateOrder(op, x.F.Owner, x.Supplier, [new(x.F.Widget, 10, 4.1256m)], "2026-01-09", "Order notes"));
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(0m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal("draft", x.S.OrderList().Single().Status);
        x.S.TransitionOrder(Guid.NewGuid(), x.F.Owner, id, "open");
        var line = x.S.OrderLines(id).Single();
        var req = new PostPurchaseRequest(Guid.NewGuid(), x.F.Owner, x.Supplier, null, "2026-01-10", null,
            [new(x.F.Widget, 4, 4.1256m, OrderLineId: line.Id)], OrderId: id);
        x.S.PostPurchase(req);
        Assert.True(x.S.PostPurchase(req).AlreadyPosted);
        Assert.Equal("partial", x.S.OrderList().Single().Status);
        Assert.Equal(6m, x.S.OrderLines(id).Single().Remaining);
        x.S.PostPurchase(req with { OperationId = Guid.NewGuid(), Lines = [new(x.F.Widget, 6, 4.1256m, OrderLineId: line.Id)] });
        Assert.Equal("received", x.S.OrderList().Single().Status);
        Assert.Equal(stock + 10m, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(41.25m, x.S.OutstandingBalance(x.Supplier)); // independently rounded receipt lines
    }

    [Fact]
    public void Overreceipt_needs_confirmation_cancel_stops_receipts_and_reverse_reopens_order()
    {
        using var x = new Ctx();
        var id = x.S.CreateOrder(Guid.NewGuid(), x.F.Owner, x.Supplier, [new(x.F.Widget, 2, 10)], null, null);
        x.S.TransitionOrder(Guid.NewGuid(), x.F.Owner, id, "open");
        var line = x.S.OrderLines(id).Single();
        var req = new PostPurchaseRequest(Guid.NewGuid(), x.F.Owner, x.Supplier, null, "2026-01-10", null,
            [new(x.F.Widget, 3, 10, OrderLineId: line.Id)], OrderId: id);
        Assert.Throws<PosException>(() => x.S.PostPurchase(req));
        Assert.Empty(x.S.InvoiceList(x.Supplier));
        var receipt = x.S.PostPurchase(req with { ConfirmOverReceipt = true });
        Assert.Equal("received", x.S.OrderList().Single().Status);
        x.S.ReversePurchase(x.F.Owner, receipt.InvoiceId, "Wrong receipt");
        Assert.Equal(2m, x.S.OrderLines(id).Single().Remaining);
        x.S.TransitionOrder(Guid.NewGuid(), x.F.Owner, id, "cancelled", "Supplier cannot deliver");
        Assert.Equal("cancelled", x.S.OrderList().Single().Status);
        Assert.Throws<PosException>(() => x.S.PostPurchase(req with { OperationId = Guid.NewGuid() }));
        using var connection = x.F.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events WHERE action='supplier.order_overreceipt'";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void Return_clears_payable_once_without_cash_and_reversal_restores_it()
    {
        using var x = new Ctx();
        var buy = x.Buy();
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        var cash = x.F.Shifts.ExpectedCash(x.F.Shift);
        var req = x.Return(buy.InvoiceId, 3);
        var result = x.S.PostReturn(req);
        Assert.True(x.S.PostReturn(req).AlreadyPosted);
        Assert.Equal(30m, result.Total);
        Assert.Equal(stock - 3, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(70m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal(70m, x.S.InvoiceList(x.Supplier).Single().Outstanding);
        Assert.Equal(cash, x.F.Shifts.ExpectedCash(x.F.Shift));
        var reverse = Guid.NewGuid();
        x.S.ReverseReturn(reverse, x.F.Owner, result.Id, "Wrong item");
        x.S.ReverseReturn(reverse, x.F.Owner, result.Id, "Wrong item");
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(100m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal(100m, x.S.InvoiceList(x.Supplier).Single().Outstanding);
        Assert.Contains(x.S.Statement(x.Supplier), s => s.Type == "return_reversal");
    }

    [Fact]
    public void Paid_invoice_return_creates_reusable_credit_and_used_credit_reversal_reopens_new_debt()
    {
        using var x = new Ctx();
        var original = x.Buy(paid: 100);
        var ret = x.S.PostReturn(x.Return(original.InvoiceId, 4));
        Assert.Equal(-40m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal(40m, x.S.AvailableCredit(x.Supplier));
        var later = x.Buy(3, 10);
        Assert.Equal(0m, x.S.InvoiceList(x.Supplier).Single(i => i.Id == later.InvoiceId).Outstanding);
        Assert.Equal(-10m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal(10m, x.S.AvailableCredit(x.Supplier));
        Assert.Single(x.S.Payments(x.Supplier)); // credit is not another cash/electronic payment
        x.S.ReverseReturn(Guid.NewGuid(), x.F.Owner, ret.Id, "Credit revoked");
        Assert.Equal(30m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal(30m, x.S.InvoiceList(x.Supplier).Single(i => i.Id == later.InvoiceId).Outstanding);
        Assert.Equal(0m, x.S.AvailableCredit(x.Supplier));
    }

    [Fact]
    public void Fractional_returns_use_original_cost_and_distribute_line_rounding_exactly()
    {
        using var x = new Ctx();
        var buy = x.Buy(0.003m, 3.3333m); // one piastre across three thousandths
        decimal total = 0;
        for (var i = 0; i < 3; i++) total += x.S.PostReturn(x.Return(buy.InvoiceId, 0.001m, 999)).Total;
        Assert.Equal(buy.Total, total);
        Assert.Equal(0m, x.S.OutstandingBalance(x.Supplier));
        Assert.Throws<PosException>(() => x.S.PostReturn(x.Return(buy.InvoiceId, 0.001m)));
    }

    [Fact]
    public void Invalid_returns_and_faults_roll_back_every_effect()
    {
        using var x = new Ctx();
        var buy = x.Buy();
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        Assert.Throws<PosException>(() => x.S.PostReturn(x.Return(buy.InvoiceId, 11)));
        Assert.Throws<PosException>(() => x.S.PostReturn(x.Return(null, stock + 1)));
        Assert.Throws<PosException>(() => x.S.PostReturn(x.Return(null, 1) with { Notes = "" }));
        x.S.FaultInjector = step => { if (step == "after_supplier_return_stock") throw new IOException("crash"); };
        var req = x.Return(buy.InvoiceId, 2);
        Assert.Throws<IOException>(() => x.S.PostReturn(req));
        Assert.Empty(x.S.ReturnList());
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(100m, x.S.OutstandingBalance(x.Supplier));
        x.S.FaultInjector = null;
        x.S.PostReturn(req);
        Assert.Equal(80m, x.S.OutstandingBalance(x.Supplier));
        Assert.Throws<PosException>(() => x.S.ReversePurchase(x.F.Owner, buy.InvoiceId, "Has return"));
    }

    [Fact]
    public void Every_new_owner_operation_refuses_cashier_even_for_duplicate_operation()
    {
        using var x = new Ctx();
        var op = Guid.NewGuid();
        var order = x.S.CreateOrder(op, x.F.Owner, x.Supplier, [new(x.F.Widget, 1, 10)], null, null);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.CreateOrder(op, x.F.Cashier, x.Supplier, [new(x.F.Widget, 1, 10)], null, null)).Code);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.TransitionOrder(Guid.NewGuid(), x.F.Cashier, order, "open")).Code);
        var req = x.Return(null, 1);
        var ret = x.S.PostReturn(req);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.PostReturn(req with { ActorId = x.F.Cashier })).Code);
        var reverse = Guid.NewGuid();
        x.S.ReverseReturn(reverse, x.F.Owner, ret.Id, "Correction");
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.S.ReverseReturn(reverse, x.F.Cashier, ret.Id, "Correction")).Code);
    }

    [Fact]
    public void Credit_applies_before_new_money_and_payment_retries_do_not_spend_credit_twice()
    {
        using var x = new Ctx();
        var original = x.Buy(paid: 100);
        x.S.PostReturn(x.Return(original.InvoiceId, 4));
        Assert.Throws<PosException>(() => x.Buy(5, 10, 11));
        var later = x.Buy(5, 10, 10);
        Assert.Equal(0m, later.Outstanding);
        Assert.Equal(0m, x.S.AvailableCredit(x.Supplier));
        Assert.Equal(0m, x.S.OutstandingBalance(x.Supplier));
        Assert.Equal(110m, x.S.Payments(x.Supplier).Sum(p => p.Amount));
    }

    [Fact]
    public void Reversing_some_rounded_returns_then_returning_again_never_creates_negative_credit()
    {
        using var x = new Ctx();
        var buy = x.Buy(0.1m, 0.2m); // 2 piastres over 100 milliunits
        var returns = Enumerable.Range(0, 100).Select(_ => x.S.PostReturn(x.Return(buy.InvoiceId, 0.001m))).ToList();
        foreach (var ret in returns.Where(r => r.Total == 0)) x.S.ReverseReturn(Guid.NewGuid(), x.F.Owner, ret.Id, "Correction");
        var remaining = 0.1m - x.S.PurchaseLines(buy.InvoiceId).Single().Returned;
        var another = x.S.PostReturn(x.Return(buy.InvoiceId, Math.Min(0.001m, remaining)));
        Assert.True(another.Total >= 0m);
        var last = remaining - 0.001m;
        if (last > 0) x.S.PostReturn(x.Return(buy.InvoiceId, last));
        Assert.Equal(0m, x.S.OutstandingBalance(x.Supplier));
    }

    [Fact]
    public void Reversing_a_credit_funded_purchase_releases_credit_without_reversing_returned_goods()
    {
        using var x = new Ctx();
        var original = x.Buy(paid: 100);
        x.S.PostReturn(x.Return(original.InvoiceId, 4));
        var before = x.F.Catalog.OnHand(x.F.Widget);
        var later = x.Buy(3, 10);
        x.S.ReversePurchase(x.F.Owner, later.InvoiceId, "Duplicate delivery");
        Assert.Equal(before, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(40m, x.S.AvailableCredit(x.Supplier));
        Assert.Equal(-40m, x.S.OutstandingBalance(x.Supplier));
        Assert.False(x.S.ReturnList().Single().Reversed);
        Assert.Equal(0m, x.S.InvoiceList(x.Supplier).Single(i => i.Id == later.InvoiceId).Outstanding);
    }

    [Fact]
    public void Reopening_paid_debt_applies_unused_credit_before_payment_preview_and_posting()
    {
        using var x = new Ctx();
        var a = x.Buy(); var b = x.Buy();
        var paid = x.S.Pay(new(Guid.NewGuid(), x.F.Owner, x.Supplier, "2026-01-11", 200, PaymentMethod.Transfer));
        x.S.PostReturn(x.Return(a.InvoiceId, 10));
        Assert.Equal(100m, x.S.AvailableCredit(x.Supplier));
        x.S.ReversePayment(x.F.Owner, paid.PaymentId, "Wrong bank payment");
        Assert.Equal(100m, x.S.InvoiceList(x.Supplier).Sum(i => i.Outstanding));
        Assert.Equal(0m, x.S.AvailableCredit(x.Supplier));
        var proposal = x.S.Propose(x.Supplier, 100);
        x.S.Pay(new(Guid.NewGuid(), x.F.Owner, x.Supplier, "2026-01-12", 100, PaymentMethod.Transfer, Allocations: proposal));
        Assert.Equal(0m, x.S.OutstandingBalance(x.Supplier));
    }

    [Fact]
    public void Migration_preserves_v11_data_and_interrupted_upgrade_rolls_back()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("pos.db");
        var old = new PosDatabase(path, 11, null);
        var clock = new SystemClock();
        var owner = new UserService(old, clock).Create("owner", "Owner", Role.Owner, "owner-test-1");
        var supplier = new SupplierService(old, clock).Create(owner, "Retained");
        using (var c = old.Open())
        using (var seed = c.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO supplier_invoices(id,number,operation_id,supplier_id,kind,invoice_date,total_cents,actor_id,created_at)
                VALUES(1,'MIG-P1','migration-purchase',@s,'purchase','2026-01-01',1000,@a,'2026-01-01');
                INSERT INTO supplier_payments(id,number,operation_id,supplier_id,pay_date,amount_cents,method,actor_id,created_at)
                VALUES(1,'MIG-SP1','migration-payment',@s,'2026-01-02',300,'transfer',@a,'2026-01-02');
                INSERT INTO supplier_payment_allocations(payment_id,invoice_id,amount_cents) VALUES(1,1,300);
                INSERT INTO supplier_ledger(supplier_id,entry_type,amount_cents,source_type,source_id,entry_date,created_at)
                VALUES(@s,'purchase',1000,'invoice',1,'2026-01-01','2026-01-01'),(@s,'payment',-300,'payment',1,'2026-01-02','2026-01-02');
                """;
            seed.Parameters.AddWithValue("@s", supplier); seed.Parameters.AddWithValue("@a", owner); seed.ExecuteNonQuery();
        }
        Assert.Throws<IOException>(() => new PosDatabase(path, 12, step => { if (step == 12) throw new IOException("interrupt"); }));
        using (var c = new PosDatabase(path, 11, null).Open())
        using (var query = c.CreateCommand())
        { query.CommandText = "SELECT SUM(amount_cents) FROM supplier_ledger"; Assert.Equal(700L, query.ExecuteScalar()); }
        var upgraded = new PosDatabase(path);
        Assert.Equal("Retained", new SupplierService(upgraded, clock).Get(supplier)!.Name);
        Assert.Equal(7m, new SupplierService(upgraded, clock).InvoiceList(supplier).Single().Outstanding);
        Assert.Equal(3m, new SupplierService(upgraded, clock).Payments(supplier).Single().Amount);
        Assert.Empty(new SupplierService(upgraded, clock).OrderList());
        Assert.NotEmpty(Directory.GetFiles(upgraded.PreMigrationDirectory, "pos-v11-*.db"));
    }

    [Fact]
    public void Encrypted_restore_retains_orders_receipts_returns_and_credit()
    {
        using var x = new Ctx();
        using var folder = new TempFolder();
        var order = x.S.CreateOrder(Guid.NewGuid(), x.F.Owner, x.Supplier, [new(x.F.Widget, 4, 10)], null, null);
        x.S.TransitionOrder(Guid.NewGuid(), x.F.Owner, order, "open");
        var orderLine = x.S.OrderLines(order).Single();
        x.S.PostPurchase(new(Guid.NewGuid(), x.F.Owner, x.Supplier, null, "2026-01-10", null,
            [new(x.F.Widget, 4, 10, OrderLineId: orderLine.Id)], 40, PaymentMethod.Transfer, OrderId: order));
        var original = x.Buy(paid: 100);
        x.S.PostReturn(x.Return(original.InvoiceId, 3));
        x.Buy(2, 10);
        var package = new BackupService(x.F.Db, x.F.Clock).CreatePackage(folder.Combine("backup"), "return-backup-secret", false);
        var path = folder.Combine("restored/pos.db");
        RestoreService.Restore(package, "return-backup-secret", path, folder.Combine("preserved"), x.F.Clock, x.F.Owner);
        var restored = new SupplierService(new PosDatabase(path), x.F.Clock);
        Assert.Equal(order, restored.OrderList().Single().Id);
        Assert.Single(restored.ReturnList());
        Assert.Equal(4m, restored.OrderLines(order).Single().Received);
        Assert.Equal("received", restored.OrderList().Single().Status);
        Assert.Equal(10m, restored.AvailableCredit(x.Supplier));
        Assert.Equal(-10m, restored.OutstandingBalance(x.Supplier));
    }
}
