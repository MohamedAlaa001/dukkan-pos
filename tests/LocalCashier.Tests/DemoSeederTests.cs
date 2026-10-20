using Dukkan.UI;
using LocalCashier.Application;
using Xunit;

namespace LocalCashier.Tests;

public class DemoSeederTests
{
    [Fact]
    public void Demo_shop_seeds_through_the_real_services_and_reconciles()
    {
        using var t = new TempFolder();
        var s = AppServices.Create(t.Combine("dukkan"), new FakePrinter());
        Assert.True(s.NeedsSetup);
        DemoSeeder.Seed(s, days: 6);
        Assert.False(s.NeedsSetup);

        var styles = s.Styles.List();
        Assert.Equal(10, styles.Count);
        Assert.All(styles, st => Assert.True(st.VariantCount >= 1));
        var today = s.Queries.Today;
        var from = DateTime.Parse(today).AddDays(-6).ToString("yyyy-MM-dd");
        var stats = s.Queries.Stats(from, today);
        Assert.True(stats.Invoices > 20);
        Assert.True(stats.Sales > 0 && stats.GrossProfit > 0);
        Assert.Equal(stats.Sales, s.Queries.Daily(from, today).Sum(d => d.Sales));
        Assert.Null(s.Queries.CurrentShift());                       // every demo shift is closed
        Assert.All(s.Queries.ShiftHistory().Where(h => !h.IsOpen), h => Assert.Equal(0m, h.Variance));
        Assert.NotNull(s.Users.Login(DemoSeeder.OwnerUser, DemoSeeder.OwnerPassword).User);
        Assert.Equal(7, s.Customers.List().Count);
        Assert.Contains(s.Customers.List(), c => c.Balance > 0);    // some credit sales
        Assert.Equal(AppServices.Create(t.Combine("dukkan"), new FakePrinter()).Queries.Stats(from, today).Invoices, stats.Invoices);   // persists
    }
}
