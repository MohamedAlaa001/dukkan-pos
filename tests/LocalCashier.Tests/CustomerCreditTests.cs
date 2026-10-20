using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using Xunit;

namespace LocalCashier.Tests;

public class CustomerCreditTests
{
    sealed class Ctx : IDisposable
    {
        public PosFixture F { get; } = new();
        public CollectionService Col { get; }
        public InstallmentService Inst { get; }
        public CustomerCreditService Credit { get; }
        public ReturnService Returns { get; }
        public long Customer { get; }

        public Ctx(decimal limit = 100000m)
        {
            Col = new CollectionService(F.Db, F.Clock);
            Inst = new InstallmentService(F.Db, F.Clock);
            Credit = new CustomerCreditService(F.Db, F.Clock);
            Returns = new ReturnService(F.Db, F.Clock);
            Customer = F.Customers.Add(F.Owner, "Sara", "0100", true, limit);
            F.Inventory.Receive(F.Owner, F.Widget, 5000, 4m);
        }

        /// <summary>600 units of the 10.00 widget = 6,000.</summary>
        public PostSaleResult Sell(decimal qty, decimal paidCash, CreditArrangement? arr = null, long? customer = null, long? approver = null, Guid? op = null)
            => F.Sales.Post(F.Sale(qty, paidCash, op, customer: customer ?? Customer, approver: approver) with { Arrangement = arr });

        public CollectResult Collect(decimal amount, PaymentMethod m = PaymentMethod.Cash, Guid? op = null, long? actor = null, IReadOnlyList<DebtAllocation>? alloc = null) =>
            Col.Collect(new CollectRequest(op ?? Guid.NewGuid(), Customer, amount, m, F.Shift, actor ?? F.Cashier, Allocations: alloc));

        public IReadOnlyList<InstallmentInput> Four(string first = "2099-01-01") => InstallmentService.EqualPlan(4000m, 4, first);
        public void Dispose() => F.Dispose();
    }

    [Fact]
    public void Sale_6000_paid_2000_leaves_4000_owed_without_extra_debt_from_a_schedule()
    {
        using var x = new Ctx();
        var res = x.Sell(600, 2000m, new CreditArrangement(null, x.Four()));
        Assert.Equal(4000m, res.Debt);
        Assert.Equal(4000m, x.F.Customers.Balance(x.Customer));
        var plan = x.Inst.Schedule(res.SaleId);
        Assert.Equal(4, plan.Count);
        Assert.All(plan, p => Assert.Equal(1000m, p.Amount));
        Assert.Equal(4000m, plan.Sum(p => p.Remaining));
        Assert.Equal(1, x.F.Count("sales"));                         // the schedule created no second sale
        Assert.Equal(1, x.F.Count("customer_ledger"));               // and no second debt entry
        Assert.All(plan, p => Assert.Equal("upcoming", p.Status));
    }

    [Fact]
    public void Equal_split_puts_the_rounding_remainder_on_the_last_installment()
    {
        Assert.Equal([3333L, 3333, 3334], InstallmentCalculator.EqualSplit(10000, 3));
        var plan = InstallmentService.EqualPlan(100m, 3, "2026-01-31");
        Assert.Equal(100m, plan.Sum(p => p.Amount));
        Assert.Equal(33.34m, plan[^1].Amount);
        Assert.Equal("2026-02-28", plan[1].DueDate);                 // month end is clamped
    }

    [Fact]
    public void Installments_that_do_not_add_up_are_rejected_without_posting()
    {
        using var x = new Ctx();
        var bad = new CreditArrangement(null, [new InstallmentInput("2099-01-01", 1000m), new InstallmentInput("2099-02-01", 2999m)]);
        Assert.Equal(PosError.PaymentMismatch, Assert.Throws<PosException>(() => x.Sell(600, 2000m, bad)).Code);
        Assert.Equal(0, x.F.Count("sales"));
        Assert.Equal(0, x.F.Count("installment_plans"));
    }

    [Fact]
    public void Partial_collection_against_one_installment_then_early_settlement()
    {
        using var x = new Ctx();
        var sale = x.Sell(600, 2000m, new CreditArrangement(null, x.Four()));
        x.Collect(400m);
        var plan = x.Inst.Schedule(sale.SaleId);
        Assert.Equal(600m, plan[0].Remaining);
        Assert.Equal("partial", plan[0].Status);
        Assert.Equal(3600m, x.F.Customers.Balance(x.Customer));

        // 1,500 spans the rest of #1 (600) and #2 (900)
        x.Collect(1500m);
        plan = x.Inst.Schedule(sale.SaleId);
        Assert.Equal([0m, 100m, 1000m, 1000m], plan.Select(p => p.Remaining).ToArray());
        Assert.Equal("paid", plan[0].Status);

        // early settlement closes everything with no penalty
        x.Collect(2100m);
        Assert.Equal(0m, x.F.Customers.Balance(x.Customer));
        Assert.All(x.Inst.Schedule(sale.SaleId), p => Assert.Equal("paid", p.Status));
        Assert.Empty(x.Col.OpenDebts(x.Customer));
    }

    [Fact]
    public void One_collection_can_cover_several_invoices()
    {
        using var x = new Ctx();
        var a = x.Sell(100, 0m, new CreditArrangement("2098-01-01"));    // 1,000
        var b = x.Sell(100, 0m, new CreditArrangement("2098-02-01"));    // 1,000
        var res = x.Collect(1500m);
        Assert.Equal(2, res.Allocations.Count);
        Assert.Equal(0m, x.Col.Outstanding(a.SaleId));
        Assert.Equal(500m, x.Col.Outstanding(b.SaleId));
        Assert.Equal(500m, x.F.Customers.Balance(x.Customer));
    }

    [Fact]
    public void Overpayment_is_rejected_without_any_effect()
    {
        using var x = new Ctx();
        x.Sell(600, 2000m, new CreditArrangement(null, x.Four()));
        var cash = x.F.Shifts.ExpectedCash(x.F.Shift);
        Assert.Equal(PosError.PaymentMismatch, Assert.Throws<PosException>(() => x.Collect(4000.01m)).Code);
        Assert.Equal(0, x.F.Count("collections"));
        Assert.Equal(0, x.F.Count("installment_allocations"));
        Assert.Equal(cash, x.F.Shifts.ExpectedCash(x.F.Shift));
        Assert.Equal(4000m, x.F.Customers.Balance(x.Customer));
    }

    [Fact]
    public void Anonymous_credit_is_rejected_and_limits_are_enforced_with_owner_override()
    {
        using var x = new Ctx(limit: 3000m);
        Assert.Equal(PosError.CreditNotAllowed, Assert.Throws<PosException>(() =>
            x.F.Sales.Post(x.F.Sale(100, 0m))).Code);                            // no customer
        Assert.Equal(PosError.CreditLimitExceeded, Assert.Throws<PosException>(() => x.Sell(400, 0m)).Code);   // 4,000 > 3,000
        Assert.Equal(0, x.F.Count("sales"));

        var res = x.Sell(400, 0m, approver: x.F.Owner);                           // owner approves
        Assert.Equal(4000m, res.Debt);
        Assert.Equal(1, x.F.Count("audit_events WHERE action='credit.limit_override'"));
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.Sell(10, 0m, approver: x.F.Cashier)).Code);
    }

    [Fact]
    public void Position_shows_balance_available_credit_and_resulting_balance_inputs()
    {
        using var x = new Ctx(limit: 5000m);
        x.Sell(200, 0m);   // 2,000
        var p = x.Credit.Position(x.Customer);
        Assert.Equal((2000m, 5000m, 3000m, true), (p.Balance, p.Limit, p.Available, p.CreditEnabled));
    }

    [Fact]
    public void Overdue_is_calculated_from_unpaid_portions()
    {
        using var x = new Ctx();
        var past = new CreditArrangement(null, [new InstallmentInput("2020-01-01", 1500m), new InstallmentInput("2020-02-01", 1500m), new InstallmentInput("2099-01-01", 1000m)]);
        var sale = x.Sell(600, 2000m, past);
        Assert.Equal(3000m, x.Credit.Summary().OverdueTotal);
        Assert.Equal(4000m, x.Credit.Summary().Outstanding);
        x.Collect(1000m);                                                          // partly pays the first overdue installment
        var s = x.Inst.Schedule(sale.SaleId);
        Assert.Equal("partial", s[0].Status);
        Assert.True(s[0].Overdue);
        Assert.Equal(2000m, x.Credit.Summary().OverdueTotal);
        Assert.Equal(2, x.Credit.DueItems(overdueOnly: true).Count);
        Assert.False(s[2].Overdue);
    }

    [Fact]
    public void Return_clears_debt_first_and_refunds_only_the_rest()
    {
        using var x = new Ctx();
        // EGP 300 sale, EGP 100 paid, EGP 250 returned: 200 of debt cleared, 50 refunded
        var sale = x.Sell(30, 100m, new CreditArrangement(null, [new InstallmentInput("2099-01-01", 100m), new InstallmentInput("2099-02-01", 100m)]));
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        var lines = x.F.Sales.FindForReturn(sale.Number)!.Lines;
        var cashBefore = x.F.Shifts.ExpectedCash(x.F.Shift);
        var ret = x.Returns.Post(new PostReturnRequest(Guid.NewGuid(), sale.SaleId, x.F.Shift, x.F.Owner, null, "wrong size",
            [new ReturnLineRequest(lines[0].SaleLineId, 25, true)], PaymentMethod.Cash));
        Assert.Equal(250m, ret.Total);
        Assert.Equal(200m, ret.DebtReduction);
        Assert.Equal(50m, ret.Refund);
        Assert.Equal(stock + 25, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(cashBefore - 50m, x.F.Shifts.ExpectedCash(x.F.Shift));
        Assert.Equal(0m, x.F.Customers.Balance(x.Customer));
        Assert.Empty(x.Inst.Schedule(sale.SaleId).Where(i => i.Remaining > 0));
        Assert.Equal(2, x.Inst.History(sale.SaleId).Count);                      // initial + return version are both kept
    }

    [Fact]
    public void Partial_return_reduces_the_latest_unpaid_installments_first_and_keeps_history()
    {
        using var x = new Ctx();
        var sale = x.Sell(600, 2000m, new CreditArrangement(null, x.Four()));
        x.Collect(400m);
        var lines = x.F.Sales.FindForReturn(sale.Number)!.Lines;
        // preview matches what gets posted
        var preview = x.Inst.PreviewReduction(sale.SaleId, 1500m);
        x.Returns.Post(new PostReturnRequest(Guid.NewGuid(), sale.SaleId, x.F.Shift, x.F.Owner, null, "damaged",
            [new ReturnLineRequest(lines[0].SaleLineId, 150, false)], null));
        var now = x.Inst.Schedule(sale.SaleId);
        Assert.Equal(preview.Select(p => p.Remaining), now.Select(p => p.Remaining));
        Assert.Equal([600m, 1000m, 500m], now.Where(p => p.Remaining > 0).Select(p => p.Remaining).ToArray());
        Assert.Equal(2100m, x.F.Customers.Balance(x.Customer));
        Assert.Equal(2, x.Inst.History(sale.SaleId).Count);
        Assert.Single(x.Inst.History(sale.SaleId), h => h.Active);
        Assert.Equal(2100m, x.Inst.Schedule(sale.SaleId).Sum(p => p.Remaining));
    }

    [Fact]
    public void Reversing_a_collection_restores_allocations_debt_and_cash_once()
    {
        using var x = new Ctx();
        var sale = x.Sell(600, 2000m, new CreditArrangement(null, x.Four()));
        var cash = x.F.Shifts.ExpectedCash(x.F.Shift);
        var col = x.Collect(1500m);
        Assert.Equal(cash + 1500m, x.F.Shifts.ExpectedCash(x.F.Shift));
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.Col.Reverse(x.F.Cashier, col.CollectionId, "oops", x.F.Shift)).Code);

        x.Col.Reverse(x.F.Owner, col.CollectionId, "keyed wrong amount", x.F.Shift);
        Assert.Equal(cash, x.F.Shifts.ExpectedCash(x.F.Shift));
        Assert.Equal(4000m, x.F.Customers.Balance(x.Customer));
        Assert.Equal(4000m, x.Inst.Schedule(sale.SaleId).Sum(p => p.Remaining));
        Assert.Equal(PosError.Duplicate, Assert.Throws<PosException>(() => x.Col.Reverse(x.F.Owner, col.CollectionId, "again", x.F.Shift)).Code);
        Assert.Equal(cash, x.F.Shifts.ExpectedCash(x.F.Shift));
        Assert.Contains(x.F.Customers.Statement(x.Customer), e => e.Type == "collection_reversal");
    }

    [Fact]
    public void Card_and_transfer_collections_do_not_touch_the_drawer()
    {
        using var x = new Ctx();
        x.Sell(600, 2000m);
        var cash = x.F.Shifts.ExpectedCash(x.F.Shift);
        x.Collect(500m, PaymentMethod.Card);
        x.Collect(500m, PaymentMethod.Transfer);
        Assert.Equal(cash, x.F.Shifts.ExpectedCash(x.F.Shift));
        Assert.Equal(3000m, x.F.Customers.Balance(x.Customer));
    }

    [Fact]
    public void Retries_do_not_duplicate_sales_or_collections()
    {
        using var x = new Ctx();
        var sop = Guid.NewGuid();
        var arr = new CreditArrangement(null, x.Four());
        x.Sell(600, 2000m, arr, op: sop);
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        Assert.True(x.Sell(600, 2000m, arr, op: sop).AlreadyPosted);
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(1, x.F.Count("sales"));
        Assert.Equal(4, x.F.Count("sale_installments"));

        var cop = Guid.NewGuid();
        x.Collect(100m, op: cop);
        Assert.True(x.Collect(100m, op: cop).AlreadyPosted);
        Assert.Equal(1, x.F.Count("collections"));
        Assert.Equal(3900m, x.F.Customers.Balance(x.Customer));
    }

    [Fact]
    public void Schedules_and_balances_survive_a_restart()
    {
        using var x = new Ctx();
        var sale = x.Sell(600, 2000m, new CreditArrangement(null, x.Four()));
        x.Collect(400m);
        var db = new PosDatabase(x.F.Db.Path);
        var inst = new InstallmentService(db, x.F.Clock);
        Assert.Equal(3600m, new CustomerService(db, x.F.Clock).Balance(x.Customer));
        Assert.Equal(600m, inst.Schedule(sale.SaleId)[0].Remaining);
    }

    [Fact]
    public void Reschedule_changes_dates_not_debt_and_keeps_history()
    {
        using var x = new Ctx();
        var sale = x.Sell(600, 2000m, new CreditArrangement(null, x.Four()));
        x.Collect(400m);
        var fresh = InstallmentService.EqualPlan(3600m, 2, "2099-06-01");
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.Inst.Reschedule(x.F.Cashier, sale.SaleId, "x", fresh)).Code);
        Assert.Equal(PosError.InvalidDiscount, Assert.Throws<PosException>(() => x.Inst.Reschedule(x.F.Owner, sale.SaleId, " ", fresh)).Code);
        Assert.Equal(PosError.PaymentMismatch, Assert.Throws<PosException>(() =>
            x.Inst.Reschedule(x.F.Owner, sale.SaleId, "x", InstallmentService.EqualPlan(3000m, 2, "2099-06-01"))).Code);

        x.Inst.Reschedule(x.F.Owner, sale.SaleId, "customer asked for a longer term", fresh);
        Assert.Equal(3600m, x.F.Customers.Balance(x.Customer));
        var now = x.Inst.Schedule(sale.SaleId);
        Assert.Equal(3600m, now.Sum(p => p.Remaining));
        Assert.Equal(400m, now.Sum(p => p.Paid));                                   // posted payment preserved
        var history = x.Inst.History(sale.SaleId);
        Assert.Equal(2, history.Count);
        Assert.Equal(4, history[0].Installments.Count);                            // previous schedule untouched
        Assert.Equal("customer asked for a longer term", history[1].Reason);
        Assert.Equal(4000m, history[0].Installments.Sum(i => i.Amount));
    }

    [Fact]
    public void Opening_balance_creates_no_sale_stock_or_revenue_and_can_be_collected()
    {
        using var x = new Ctx();
        var stock = x.F.Catalog.OnHand(x.F.Widget);
        var sales = new ReportService(x.F.Db).Summary("2000-01-01", "2099-12-31");
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.Credit.AddOpeningBalance(Guid.NewGuid(), x.F.Cashier, x.Customer, 900m, null)).Code);
        var op = Guid.NewGuid();
        x.Credit.AddOpeningBalance(op, x.F.Owner, x.Customer, 900m, "old notebook");
        x.Credit.AddOpeningBalance(op, x.F.Owner, x.Customer, 900m, "old notebook");   // retry
        Assert.Equal(900m, x.F.Customers.Balance(x.Customer));
        Assert.Equal(0, x.F.Count("sales"));
        Assert.Equal(stock, x.F.Catalog.OnHand(x.F.Widget));
        Assert.Equal(sales.GrossSales, new ReportService(x.F.Db).Summary("2000-01-01", "2099-12-31").GrossSales);
        Assert.Equal("opening", x.F.Customers.Statement(x.Customer).Single().Type);

        x.Collect(400m);
        Assert.Equal(500m, x.F.Customers.Balance(x.Customer));
        Assert.Equal(PosError.PaymentMismatch, Assert.Throws<PosException>(() => x.Collect(500.01m)).Code);
    }

    [Fact]
    public void Collections_do_not_increase_sales_totals()
    {
        using var x = new Ctx();
        x.Sell(600, 2000m);
        var report = new ReportService(x.F.Db);
        var before = report.Summary("2000-01-01", "2099-12-31");
        x.Collect(1000m);
        x.Collect(500m, PaymentMethod.Card);
        var after = report.Summary("2000-01-01", "2099-12-31");
        Assert.Equal(before.GrossSales, after.GrossSales);
        Assert.Equal(before.SaleCount, after.SaleCount);
        Assert.Equal(before.NetSalesExTax, after.NetSalesExTax);
        Assert.Equal(before.PaymentsByMethod, after.PaymentsByMethod);
    }

    [Fact]
    public void Owner_can_adjust_allocation_but_cashier_cannot()
    {
        using var x = new Ctx();
        var a = x.Sell(100, 0m, new CreditArrangement("2098-01-01"));
        var b = x.Sell(100, 0m, new CreditArrangement("2098-02-01"));
        var pick = new[] { new DebtAllocation(b.SaleId, null, null, 700m) };
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => x.Collect(700m, alloc: pick)).Code);
        x.Collect(700m, actor: x.F.Owner, alloc: pick);
        Assert.Equal(1000m, x.Col.Outstanding(a.SaleId));
        Assert.Equal(300m, x.Col.Outstanding(b.SaleId));
        Assert.Equal(PosError.PaymentMismatch, Assert.Throws<PosException>(() =>
            x.Collect(100m, actor: x.F.Owner, alloc: [new DebtAllocation(b.SaleId, null, null, 400m)])).Code);
    }
}

static class TestExt
{
    public static T Tap<T>(this T v, Action<T> a) { a(v); return v; }
}
