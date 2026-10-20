using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using Xunit;

namespace LocalCashier.Tests;

public class AdminTests
{
    [Fact]
    public void Five_failures_lock_the_account_and_success_clears_the_counter()
    {
        using var f = new PosFixture();
        var r = f.Users.Login("cash", "wrong");
        Assert.Equal(AuthFailure.BadCredentials, r.Failure);
        Assert.Equal(4, r.AttemptsLeft);
        Assert.NotNull(f.Users.Login("cash", "cash-pass").User);
        for (var i = 0; i < 4; i++) f.Users.Login("cash", "bad");
        var locked = f.Users.Login("cash", "bad");
        Assert.Equal(AuthFailure.Locked, locked.Failure);
        Assert.NotNull(locked.LockedUntil);
        Assert.Equal(AuthFailure.Locked, f.Users.Login("cash", "cash-pass").Failure);
        Assert.True(f.Users.List().Single(u => u.Username == "cash").Locked);
        f.Users.ResetPassword(f.Owner, f.Cashier, "new-pass-123");
        Assert.NotNull(f.Users.Login("cash", "new-pass-123").User);
    }

    [Fact]
    public void Owner_manages_users_with_password_rules_and_cannot_remove_the_last_owner()
    {
        using var f = new PosFixture();
        Assert.Equal(PosError.WeakPassword, Assert.Throws<PosException>(() => f.Users.CreateByOwner(f.Owner, "new", "New", Role.Cashier, "short")).Code);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => f.Users.CreateByOwner(f.Cashier, "x", "X", Role.Cashier, "long-enough-1")).Code);
        var id = f.Users.CreateByOwner(f.Owner, "sara", "Sara", Role.Cashier, "long-enough-1");
        Assert.Equal(PosError.Duplicate, Assert.Throws<PosException>(() => f.Users.CreateByOwner(f.Owner, "sara", "Sara 2", Role.Cashier, "long-enough-1")).Code);
        f.Users.SetActive(f.Owner, id, false);
        Assert.Equal(AuthFailure.BadCredentials, f.Users.Login("sara", "long-enough-1").Failure);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => f.Users.SetActive(f.Owner, f.Owner, false)).Code);
        Assert.Null(f.Users.ApproveAsOwner("cash", "cash-pass"));
        Assert.Equal(f.Owner, f.Users.ApproveAsOwner("owner", "owner-pass"));
    }

    [Fact]
    public void Recovery_codes_reset_the_owner_password_once_and_are_replaced()
    {
        using var f = new PosFixture();
        var codes = f.Users.GenerateRecoveryCodes(f.Owner);
        Assert.Equal(8, codes.Count);
        Assert.Equal(8, f.Users.RemainingRecoveryCodes(f.Owner));
        Assert.False(f.Users.ResetWithRecoveryCode("owner", "AAAA-BBBB-CCCC", "brand-new-pass").Ok);
        var (ok, replacement) = f.Users.ResetWithRecoveryCode("owner", codes[0].ToLowerInvariant(), "brand-new-pass");
        Assert.True(ok);
        Assert.NotNull(replacement);
        Assert.NotNull(f.Users.Login("owner", "brand-new-pass").User);
        Assert.False(f.Users.ResetWithRecoveryCode("owner", codes[0], "another-pass-1").Ok);
        Assert.Equal(8, f.Users.RemainingRecoveryCodes(f.Owner));
        Assert.Contains(new AuditService(f.Db).List("recovery"), a => a.Action == "recovery.used");
    }

    [Fact]
    public void Held_carts_are_drafts_that_do_not_touch_stock_or_sales()
    {
        using var f = new PosFixture();
        var held = new HeldCartService(f.Db, f.Clock);
        var id = held.Save(f.Cashier, null, "Customer at fitting room", [new HeldLine(f.Widget, 2)]);
        Assert.Equal(0, f.Count("sales"));
        Assert.Equal(10m, f.Catalog.OnHand(f.Widget));
        Assert.Equal(1, Assert.Single(held.List()).ItemCount);
        var lines = held.Resume(id);
        Assert.Equal(2m, lines.Single().Quantity);
        Assert.Empty(held.List());
        Assert.Equal(PosError.NotFound, Assert.Throws<PosException>(() => held.Resume(id)).Code);
    }

    static long FirstLine(PosFixture f, long saleId)
    {
        using var c = f.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM sale_lines WHERE sale_id=@s ORDER BY line_no LIMIT 1";
        cmd.Parameters.AddWithValue("@s", saleId);
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Dashboard_invoice_and_movement_queries_reconcile_with_sales_and_returns()
    {
        using var f = new PosFixture();
        var q = new QueryService(f.Db, f.Clock);
        var returns = new ReturnService(f.Db, f.Clock);
        var today = q.Today;
        f.Inventory.Receive(f.Owner, f.Widget, 20, 4m, reference: "PO-77");
        var s1 = f.Sales.Post(f.Sale(3, 30m));
        var credit = f.Customers.Add(f.Owner, "Credit Co", creditEnabled: true, creditLimit: 500m);
        f.Sales.Post(f.Sale(2, 5m, customer: credit));
        returns.Post(new PostReturnRequest(Guid.NewGuid(), s1.SaleId, f.Shift, f.Owner, null, "wrong size",
            [new ReturnLineRequest(FirstLine(f, s1.SaleId), 1, true)], PaymentMethod.Cash));

        var stats = q.Stats(today, today);
        Assert.Equal(40m, stats.Sales);
        Assert.Equal(2, stats.Invoices);
        Assert.Equal(4, stats.Units);
        Assert.Equal(40m, q.Daily(today, today).Single().Sales);

        var inv = q.Invoices(today, today);
        Assert.Equal(2, inv.Count);
        Assert.Equal("Partial", inv.Single(i => i.Customer == "Credit Co").StatusKey);
        Assert.Equal("Paid", inv.Single(i => i.Customer is null).StatusKey);

        var moves = q.Movements(today, today);
        Assert.Contains(moves, m => m.TypeKey == "Purchase" && m.Reference == "PO-77" && m.Qty == 20);
        Assert.Contains(moves, m => m.TypeKey == "Sale" && m.Reference == s1.Number && m.Qty == -3);
        Assert.Contains(moves, m => m.TypeKey == "Return" && m.Qty == 1);
        Assert.Single(q.TopSellers(today, today, 5));
        Assert.InRange(q.Categories(today, today).Sum(c => c.Percent), 99.9, 100.1);
    }

    [Fact]
    public void Shift_and_diagnostics_info_are_available()
    {
        using var f = new PosFixture();
        var q = new QueryService(f.Db, f.Clock);
        var shift = q.CurrentShift()!;
        Assert.Equal("Cashier", shift.CashierName);
        Assert.Equal(500m, shift.OpeningFloat);
        Assert.Single(q.ShiftHistory());
        var info = q.Diagnostics();
        Assert.Equal(PosDatabase.SchemaVersion, info.SchemaVersion);
        Assert.True(info.DatabaseBytes > 0);
        Assert.True(info.FreeDiskBytes > 0);
    }
}
