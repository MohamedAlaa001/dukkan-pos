using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class SmartReportTests
{
    [Fact] public void Period_discount_reversals_use_the_saved_promotion_allocation()
    {
        using var f=new PosFixture();new PromotionService(f.Db,f.Clock).Save(Guid.NewGuid(),f.Owner,new(0,"Offer","Offer",PromotionKind.FixedCart){AmountCents=100});
        var sale=f.Sales.Post(f.Sale(3,30));var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();
        using(var c=f.Db.Open()){using var q=c.CreateCommand();q.CommandText="UPDATE sales SET business_date='2026-01-01';UPDATE shifts SET business_date='2026-01-02'";q.ExecuteNonQuery();}
        new ReturnService(f.Db,f.Clock).Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Return",[new(line.SaleLineId,1,true)],null));new CashService(f.Db,f.Clock).CloseShift(f.Cashier,f.Shift,f.Shifts.ExpectedCash(f.Shift),null);
        var next=f.Shifts.Open(f.Cashier,500);using(var c=f.Db.Open()){using var q=c.CreateCommand();q.CommandText="UPDATE shifts SET business_date='2026-01-03' WHERE id=$s";q.Parameters.AddWithValue("$s",next);q.ExecuteNonQuery();}
        new ReturnService(f.Db,f.Clock).Post(new(Guid.NewGuid(),sale.SaleId,next,f.Cashier,f.Owner,"Return",[new(line.SaleLineId,1,true)],null));
        Assert.Equal(-.33m,new ReportService(f.Db).Profit("2026-01-03","2026-01-03",ProfitGroup.Product).Single().Discounts);
    }
    [Fact] public void Shift_filter_uses_user_identity_even_when_display_names_match()
    {
        using var f=new PosFixture();var other=f.Users.Create("other","Cashier",Role.Cashier,"other-pass");new CashService(f.Db,f.Clock).CloseShift(f.Cashier,f.Shift,500,null);f.Shifts.Open(other,10);
        var q=new QueryService(f.Db,f.Clock);Assert.Single(q.ShiftHistory(100,f.Cashier));Assert.Equal(f.Shift,q.ShiftHistory(100,f.Cashier).Single().Id);
    }
    [Fact] public void Schema15_report_indexes_preserve_data_and_interrupted_upgrade_rolls_back()
    {
        using var dir=new TempFolder();var path=dir.Combine("pos.db");var old=new PosDatabase(path,15,null);var clock=new SystemClock();var owner=new UserService(old,clock).Create("owner","Owner",Role.Owner,"owner-test-1");
        var shift=new ShiftService(old,clock).Open(owner,123);
        Assert.Throws<IOException>(()=>new PosDatabase(path,16,step=>{if(step==16)throw new IOException("Interrupted");}));
        using(var c=new PosDatabase(path,15,null).Open())using(var q=c.CreateCommand()){q.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='stock_report_balance'";Assert.Equal(0L,q.ExecuteScalar());}
        var upgraded=new PosDatabase(path);Assert.Equal(123,new ShiftService(upgraded,clock).ExpectedCash(shift));Assert.NotEmpty(Directory.GetFiles(upgraded.PreMigrationDirectory,"pos-v15-*.db"));
    }
    [Fact] public void Cross_period_returns_follow_posting_actor_and_leave_original_sale_period_intact()
    {
        using var f=new PosFixture();var sale=f.Sales.Post(f.Sale(2,20));var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();new ReturnService(f.Db,f.Clock).Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Owner,null,"Return",[new(line.SaleLineId,1,true)],null));
        using(var c=f.Db.Open()){using var q=c.CreateCommand();q.CommandText="UPDATE sales SET business_date='2026-01-01';UPDATE shifts SET business_date='2026-02-01'";q.ExecuteNonQuery();}
        var report=new ReportService(f.Db);Assert.Equal(20,report.Profit("2026-01-01","2026-01-01",ProfitGroup.Product,f.Cashier).Sum(r=>r.Revenue));
        Assert.Empty(report.Profit("2026-02-01","2026-02-01",ProfitGroup.Product,f.Cashier));var returned=report.Profit("2026-02-01","2026-02-01",ProfitGroup.Cashier,f.Owner).Single();Assert.Equal(-10,returned.Revenue);Assert.Equal(-4,returned.Cost);Assert.Equal(10,returned.Returns);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void Profit_groups_reconcile_with_snapshots_and_full_returns(bool inclusive)
    {
        using var f=new PosFixture(false);f.Settings.ConfigureTax(f.Owner,inclusive);
        using(var c=f.Db.Open()){using var cmd=c.CreateCommand();cmd.CommandText="UPDATE products SET tax_rate_bp=1400 WHERE id=$p";cmd.Parameters.AddWithValue("$p",f.Widget);cmd.ExecuteNonQuery();}
        var sale=f.Sales.Post(f.Sale(3,40,orderDiscount:new Discount(DiscountKind.Fixed,1),approver:f.Owner));
        var report=new ReportService(f.Db);var date=DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var original=report.Profit(date,date,ProfitGroup.Product).Single();
        Assert.Equal(12m,original.Cost);Assert.Equal(report.Summary(date,date).NetSalesExTax,original.Revenue);
        var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();
        for(var i=0;i<3;i++)new ReturnService(f.Db,f.Clock).Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Return",[new(line.SaleLineId,1,true)],null));
        foreach(var group in Enum.GetValues<ProfitGroup>())
        {
            var rows=report.Profit(date,date,group);Assert.Equal(0,rows.Sum(r=>r.Revenue));Assert.Equal(0,rows.Sum(r=>r.Cost));Assert.Equal(0,rows.Sum(r=>r.Discounts));Assert.Equal(0,rows.Sum(r=>r.Quantity));
        }
        Assert.Equal(0,report.TaxSummary(date,date).Sum(r=>r.Tax));
    }
    [Fact] public void Three_partial_returns_reverse_the_original_stored_cost_exactly()
    {
        using var f=new PosFixture();using(var c=f.Db.Open()){using var cmd=c.CreateCommand();cmd.CommandText="UPDATE products SET avg_cost_x10000=3333 WHERE id=$p";cmd.Parameters.AddWithValue("$p",f.Widget);cmd.ExecuteNonQuery();}
        var sale=f.Sales.Post(f.Sale(3,30));var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();
        for(var i=0;i<3;i++)new ReturnService(f.Db,f.Clock).Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Return",[new(line.SaleLineId,1,true)],null));
        Assert.Equal(0,new ReportService(f.Db).Summary(DateTimeOffset.Now.ToString("yyyy-MM-dd"),DateTimeOffset.Now.ToString("yyyy-MM-dd")).CostOfGoods);
    }
    [Fact] public void Current_stock_includes_archived_stock_and_dead_stock_ignores_returns()
    {
        using var f=new PosFixture();var report=new ReportService(f.Db);var today=DateTimeOffset.Now.ToString("yyyy-MM-dd");
        Assert.Equal(40m,report.StockValuation().Sum(r=>r.CostValue));Assert.Equal(100m,report.StockValuation().Sum(r=>r.RetailValue));
        using(var c=f.Db.Open()){using var cmd=c.CreateCommand();cmd.CommandText="UPDATE products SET allow_decimal=1";cmd.ExecuteNonQuery();}
        Assert.Single(report.DeadStock(today,30));f.Sales.Post(f.Sale(.5m,5));Assert.Empty(report.DeadStock(today,30));
        using(var c=f.Db.Open()){using var cmd=c.CreateCommand();cmd.CommandText="UPDATE products SET active=0";cmd.ExecuteNonQuery();}
        Assert.Equal(9.5m,report.StockValuation().Single().Quantity);
    }
    [Fact] public void Daily_summary_is_cashier_filtered_and_keeps_open_drawers_uncounted()
    {
        using var f=new PosFixture();f.Sales.Post(f.Sale(2,20));var report=new ReportService(f.Db);var date=DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var day=report.DailySummary(date,f.Cashier);Assert.Equal(20,day.Sales);Assert.Equal(520,day.ExpectedCash);Assert.Null(day.CountedCash);Assert.Equal(1,day.OpenShifts);
        Assert.Equal(0,report.DailySummary(date,f.Owner).Sales);Assert.Empty(report.Profit("2000-01-01","2000-01-01",ProfitGroup.Product));
        new CashService(f.Db,f.Clock).CloseShift(f.Cashier,f.Shift,520,null);Assert.Equal(520,report.DailySummary(date,f.Cashier).CountedCash);
    }
}
