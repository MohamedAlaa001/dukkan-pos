using System.Reflection;
using System.Runtime.ExceptionServices;
using Dukkan.UI;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Tests;

public class StorageAndClockTests
{
    [Fact]
    public void A_real_sqlite_full_error_rolls_back_the_sale_and_allows_retry_after_space_is_available()
    {
        using var fixture = new PosFixture();
        var second = fixture.Catalog.AddProduct(fixture.Owner, "W2", "002", "Second widget", null, 10m);
        fixture.Inventory.Receive(fixture.Owner, second, 10, 4m);
        var request = fixture.Sale(1, 20m) with { Lines = [new SaleLineRequest(fixture.Widget, 1), new SaleLineRequest(second, 1)] };
        var actualFullObserved = false;
        using (var connection = fixture.Db.Open())
        {
            using var cap = connection.CreateCommand();
            cap.CommandText = "PRAGMA page_count";
            var pages = Convert.ToInt64(cap.ExecuteScalar());
            cap.CommandText = $"PRAGMA max_page_count={pages}";
            cap.ExecuteScalar();
            fixture.Sales.FaultInjector = step =>
            {
                if (step != "after_money") return;
                using var grow = connection.CreateCommand();
                grow.CommandText = "INSERT INTO audit_events(ts,action,details) VALUES('2026-10-19','full-check',zeroblob(1048576))";
                grow.ExecuteNonQuery(); // force native SQLITE_FULL after stock and cash have already been written
            };

            // The page cap is connection-local. Exercise the actual posting core and transaction wrapper
            // on that capped connection instead of faking an IOException or filling the user's drive.
            var core = typeof(SaleService).GetMethod("PostInTransaction", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var transaction = typeof(PosDatabase).Assembly.GetType("LocalCashier.Persistence.Db")!
                .GetMethod("InTransaction", BindingFlags.Static | BindingFlags.Public)!.MakeGenericMethod(typeof(PostSaleResult));
            Func<PostSaleResult> work = () =>
            {
                try { return Invoke<PostSaleResult>(core, fixture.Sales, [connection, request]); }
                catch (SqliteException ex) { actualFullObserved = ex.SqliteErrorCode == 13; throw; }
            };
            var error = Assert.Throws<PosException>(() => Invoke<PostSaleResult>(transaction, null, [connection, work]));
            Assert.True(actualFullObserved, "The engine must raise SQLITE_FULL, not a simulated exception");
            Assert.Equal(PosError.StorageFull, error.Code);
            Assert.Equal(13, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
            Assert.Contains("space", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        fixture.Sales.FaultInjector = null;
        Assert.Equal(0, fixture.Count("sales"));
        Assert.Equal(0, fixture.Count("sale_lines"));
        Assert.Equal(0, fixture.Count("payments"));
        Assert.Equal(0, fixture.Count("print_jobs"));
        Assert.Equal(10m, fixture.Catalog.OnHand(fixture.Widget));
        Assert.Equal(10m, fixture.Catalog.OnHand(second));
        Assert.Equal(500m, fixture.Shifts.ExpectedCash(fixture.Shift));

        fixture.Sales.Post(request);
        fixture.Sales.Post(request);
        Assert.Equal(1, fixture.Count("sales"));
        Assert.Equal(9m, fixture.Catalog.OnHand(fixture.Widget));
        Assert.Equal(9m, fixture.Catalog.OnHand(second));
        Assert.Equal(520m, fixture.Shifts.ExpectedCash(fixture.Shift));
    }

    [Fact]
    public void Clock_jumps_keep_posted_debt_cash_document_numbers_and_open_shift_business_date_consistent()
    {
        using var folder = new TempFolder();
        var services = AppServices.Create(folder.Combine("clock"), new FakePrinter());
        services.Clock.Set(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.FromHours(3)));
        var owner = services.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
        services.Settings.ConfigureTax(owner, true);
        var product = services.Catalog.AddProduct(owner, "W1", "001", "Widget", null, 10m);
        services.Inventory.Receive(owner, product, 10, 4m);
        var customer = services.Customers.Add(owner, "Customer", null, true, 1000m);
        var shift = services.Shifts.Open(owner, 500m);
        var first = services.Sales.Post(new PostSaleRequest(Guid.NewGuid(), shift, owner, customer,
            [new SaleLineRequest(product, 3)], null, [new PaymentRequest(PaymentMethod.Cash, 10m)], null,
            new CreditArrangement(null, [new InstallmentInput("2026-10-20", 10m), new InstallmentInput("2026-11-20", 10m)])));
        services.Clock.Set(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours(3)));
        Assert.Equal(10m, services.Credit.Summary(customer).OverdueTotal);
        services.Collections.Collect(new CollectRequest(Guid.NewGuid(), customer, 4m, PaymentMethod.Cash, shift, owner));
        services.Clock.Set(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(3)));
        Assert.Equal("2026-10-18", services.Queries.Today);
        Assert.Equal(16m, services.Customers.Balance(customer));
        Assert.Equal(0m, services.Credit.Summary(customer).OverdueTotal);
        var secondSale = services.Sales.Post(new PostSaleRequest(Guid.NewGuid(), shift, owner, null,
            [new SaleLineRequest(product, 1)], null, [new PaymentRequest(PaymentMethod.Cash, 10m)]));
        Assert.Equal("S1-000001", first.Number);
        Assert.Equal("S1-000002", secondSale.Number);
        Assert.Equal(2, services.Queries.Invoices("2026-10-19", "2026-10-19").Count);
        services.Clock.Set(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.FromHours(3)));
        Assert.Equal(16m, services.Credit.Summary(customer).OverdueTotal);
        Assert.Equal(new[] { 6m, 10m }, services.Installments.Schedule(first.SaleId).Select(i => i.Remaining));
        Assert.Equal(524m, services.Shifts.ExpectedCash(shift));
        Assert.Equal(6m, services.Catalog.OnHand(product));
    }

    static T Invoke<T>(MethodInfo method, object? target, object?[] arguments)
    {
        try { return (T)method.Invoke(target, arguments)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
