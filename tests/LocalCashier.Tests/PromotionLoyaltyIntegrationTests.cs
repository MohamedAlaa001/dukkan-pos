using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class PromotionLoyaltyIntegrationTests
{
    [Fact] public void Expiry_reconciliation_preserves_expiry_of_an_intervening_earning_that_funded_the_return_debit()
    {
        using var f=new PosFixture();var clock=new Clock(f.Clock.Now);var loyalty=new LoyaltyService(f.Db,clock);var customer=f.Customers.Add(f.Owner,"Member");
        loyalty.Configure(f.Owner,new(true,1,100,1,1));var sales=new SaleService(f.Db,clock);var earning=sales.Post(f.Sale(1,10,customer:customer));
        var redemption=sales.Post(f.Sale(1,0,customer:customer) with {RedeemPoints=10});var returns=new ReturnService(f.Db,clock);
        void Return(PostSaleResult sale){var line=sales.FindForReturn(sale.Number)!.Lines.Single();returns.Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,1,true)],null));}
        Return(earning);Assert.Equal(-10,loyalty.Balance(customer));sales.Post(f.Sale(1,10,customer:customer));Assert.Equal(0,loyalty.Balance(customer));
        clock.Now=clock.Now.AddDays(2);Return(redemption);Assert.Equal(0,loyalty.Balance(customer));loyalty.ExpireDue(customer);Assert.Equal(0,loyalty.Balance(customer));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Earn_spend_and_return_both_sales_net_to_zero_even_if_restored_points_expired(bool earningReturnedFirst)
    {
        using var f=new PosFixture();var clock=new Clock(f.Clock.Now);var loyalty=new LoyaltyService(f.Db,clock);var customer=f.Customers.Add(f.Owner,"Member");
        loyalty.Configure(f.Owner,new(true,1,100,1,1));var sales=new SaleService(f.Db,clock);var earning=sales.Post(f.Sale(1,10,customer:customer));
        var redemption=sales.Post(f.Sale(1,0,customer:customer) with {RedeemPoints=10});clock.Now=clock.Now.AddDays(2);var returns=new ReturnService(f.Db,clock);
        foreach(var sale in earningReturnedFirst?new[]{earning,redemption}:new[]{redemption,earning})
        {
            var line=sales.FindForReturn(sale.Number)!.Lines.Single();returns.Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,1,true)],null));
        }
        Assert.Equal(0,loyalty.Balance(customer));Assert.Equal(500m,f.Shifts.ExpectedCash(f.Shift));
    }
    [Fact] public void Expiry_after_partial_return_keeps_only_the_remaining_entitlement_exempt()
    {
        using var f=new PosFixture();var clock=new Clock(f.Clock.Now);var loyalty=new LoyaltyService(f.Db,clock);var customer=f.Customers.Add(f.Owner,"Member");
        loyalty.Configure(f.Owner,new(true,1,10,1,1));var sales=new SaleService(f.Db,clock);var sale=sales.Post(f.Sale(3,30,customer:customer));var line=sales.FindForReturn(sale.Number)!.Lines.Single();var returns=new ReturnService(f.Db,clock);
        var request=new PostReturnRequest(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,1,true)],null);
        returns.Post(request);Assert.Equal(20,loyalty.Balance(customer));clock.Now=clock.Now.AddDays(2);loyalty.ExpireDue(customer);Assert.Equal(0,loyalty.Balance(customer));
        returns.Post(request with {OperationId=Guid.NewGuid(),Lines=[new(line.SaleLineId,2,true)]});Assert.Equal(0,loyalty.Balance(customer));
    }
    [Fact] public void Returning_unused_expired_earnings_does_not_deduct_points_twice()
    {
        using var f=new PosFixture();var clock=new Clock(f.Clock.Now);var loyalty=new LoyaltyService(f.Db,clock);var customer=f.Customers.Add(f.Owner,"Member");
        loyalty.Configure(f.Owner,new(true,1,10,1,1));var sales=new SaleService(f.Db,clock);var sale=sales.Post(f.Sale(1,10,customer:customer));
        clock.Now=clock.Now.AddDays(2);var line=sales.FindForReturn(sale.Number)!.Lines.Single();
        new ReturnService(f.Db,clock).Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,1,true)],null));
        Assert.Equal(0,loyalty.Balance(customer));
    }
    [Fact] public void Earn_reversal_rounding_uses_cumulative_returned_quantity()
    {
        static long Run(bool split)
        {
            using var f=new PosFixture();var loyalty=new LoyaltyService(f.Db,f.Clock);var customer=f.Customers.Add(f.Owner,"Member");loyalty.Configure(f.Owner,new(true,0.067m,10,1,0));
            var sale=f.Sales.Post(f.Sale(3,30,customer:customer));var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();var returns=new ReturnService(f.Db,f.Clock);
            var request=new PostReturnRequest(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,split?1:2,true)],null);
            returns.Post(request);if(split)returns.Post(request with {OperationId=Guid.NewGuid()});return loyalty.Balance(customer);
        }
        Assert.Equal(Run(false),Run(true));Assert.Equal(1,Run(true));
    }
    sealed class Clock(DateTimeOffset now):IClock {public DateTimeOffset Now {get;set;}=now;}
    [Fact] public void Schema14_migration_keeps_shop_data_and_interruption_rolls_back_all_new_tables()
    {
        using var dir=new TempFolder();var path=dir.Combine("pos.db");var old=new PosDatabase(path,14,null);var clock=new SystemClock();
        var owner=new UserService(old,clock).Create("owner","Owner",Role.Owner,"owner-test-1");
        var customer=new CustomerService(old,clock).Add(owner,"Original customer");var shift=new ShiftService(old,clock).Open(owner,500);
        Assert.Throws<IOException>(()=>new PosDatabase(path,15,step=>{if(step==15)throw new IOException("Interrupted");}));
        using(var c=new PosDatabase(path,14,null).Open())using(var q=c.CreateCommand())
        {q.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name IN('loyalty_ledger','promotions')";Assert.Equal(0L,q.ExecuteScalar());q.CommandText="PRAGMA user_version";Assert.Equal(14L,q.ExecuteScalar());}
        var upgraded=new PosDatabase(path);Assert.False(new LoyaltyService(upgraded,clock).Settings().Enabled);
        Assert.Equal("",new PromotionService(upgraded,clock).CustomerGroup(customer));Assert.Equal(500m,new ShiftService(upgraded,clock).ExpectedCash(shift));
        Assert.Equal(9,new PaymentMethodService(upgraded,clock).List(false).Count);Assert.NotEmpty(Directory.GetFiles(upgraded.PreMigrationDirectory,"pos-v14-*.db"));
    }
    [Fact] public void Failure_after_money_rolls_back_promotions_points_tenders_and_stock_before_retry()
    {
        using var f=new PosFixture();var loyalty=new LoyaltyService(f.Db,f.Clock);var customer=f.Customers.Add(f.Owner,"Member");
        loyalty.Configure(f.Owner,new(true,1,10,1,0));loyalty.Adjust(Guid.NewGuid(),f.Owner,customer,100,"Opening");
        new PromotionService(f.Db,f.Clock).Save(Guid.NewGuid(),f.Owner,new(0,"Offer","عرض",PromotionKind.Percentage){Percent=10});
        var request=f.Sale(2,13,customer:customer) with {RedeemPoints=50};
        f.Sales.FaultInjector=step=>{if(step=="after_money")throw new IOException("Power loss");};Assert.Throws<IOException>(()=>f.Sales.Post(request));
        Assert.Equal(100,loyalty.Balance(customer));Assert.Equal(0,f.Count("sales"));Assert.Equal(0,f.Count("applied_promotions"));Assert.Equal(10m,f.Catalog.OnHand(f.Widget));
        f.Sales.FaultInjector=null;Assert.Equal(18m,f.Sales.Post(request).Total);Assert.True(f.Sales.Post(request).AlreadyPosted);
        Assert.Equal(63,loyalty.Balance(customer));
    }
    [Fact] public void Credit_reduction_precedes_refund_and_full_return_reconciles_points_and_cash()
    {
        using var f=new PosFixture();var loyalty=new LoyaltyService(f.Db,f.Clock);var customer=f.Customers.Add(f.Owner,"Member",creditEnabled:true,creditLimit:100);
        loyalty.Configure(f.Owner,new(true,1,10,1,0));loyalty.Adjust(Guid.NewGuid(),f.Owner,customer,100,"Opening");
        var sale=f.Sales.Post(f.Sale(3,5,customer:customer) with {RedeemPoints=100});Assert.Equal(15m,sale.Debt);
        var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();var returns=new ReturnService(f.Db,f.Clock);
        var request=new PostReturnRequest(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Partial",[new(line.SaleLineId,1,true)],null);
        var first=returns.Post(request);Assert.Equal(10m,first.DebtReduction);Assert.Equal(0m,first.Refund);
        returns.Post(request with {OperationId=Guid.NewGuid()});returns.Post(request with {OperationId=Guid.NewGuid()});
        Assert.Equal(100,loyalty.Balance(customer));Assert.Equal(500m,f.Shifts.ExpectedCash(f.Shift));Assert.Equal(0m,f.Customers.Balance(customer));
    }
    [Fact] public void Returning_a_sale_after_its_earned_points_were_spent_preserves_a_negative_balance()
    {
        using var f=new PosFixture();var loyalty=new LoyaltyService(f.Db,f.Clock);var customer=f.Customers.Add(f.Owner,"Member");loyalty.Configure(f.Owner,new(true,1,100,1,0));
        var original=f.Sales.Post(f.Sale(1,10,customer:customer));f.Sales.Post(f.Sale(1,0,customer:customer) with {RedeemPoints=10});
        var line=f.Sales.FindForReturn(original.Number)!.Lines.Single();new ReturnService(f.Db,f.Clock).Post(new(Guid.NewGuid(),original.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,1,true)],null));
        Assert.Equal(-10,loyalty.Balance(customer));Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,0,customer:customer) with {RedeemPoints=10}));
        f.Sales.Post(f.Sale(1,10,customer:customer));Assert.Equal(0,loyalty.Balance(customer));
    }
    [Fact] public void Restored_redemption_keeps_original_expiry_and_cannot_resurrect_expired_points()
    {
        using var f=new PosFixture();var clock=new Clock(f.Clock.Now);var loyalty=new LoyaltyService(f.Db,clock);var customer=f.Customers.Add(f.Owner,"Member");
        loyalty.Configure(f.Owner,new(true,1,10,1,1));loyalty.Adjust(Guid.NewGuid(),f.Owner,customer,100,"Opening");
        var sales=new SaleService(f.Db,clock);var sale=sales.Post(f.Sale(1,0,customer:customer) with {RedeemPoints=100});clock.Now=clock.Now.AddDays(2);
        var line=sales.FindForReturn(sale.Number)!.Lines.Single();new ReturnService(f.Db,clock).Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,1,true)],null));
        Assert.Equal(0,loyalty.Balance(customer));Assert.Equal(100,loyalty.History(customer).Single(e=>e.Kind=="reverse_redeem").Points);Assert.Equal(-100,loyalty.History(customer).Single(e=>e.Kind=="expire").Points);
    }
    [Fact] public void Money_method_overrides_still_restore_all_original_points_across_partial_returns()
    {
        using var f=new PosFixture();var loyalty=new LoyaltyService(f.Db,f.Clock);var customer=f.Customers.Add(f.Owner,"Member");loyalty.Configure(f.Owner,new(true,1,10,1,0));
        loyalty.Adjust(Guid.NewGuid(),f.Owner,customer,100,"Opening");var sale=f.Sales.Post(f.Sale(3,20,customer:customer) with {RedeemPoints=100});var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();var returns=new ReturnService(f.Db,f.Clock);
        var request=new PostReturnRequest(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,1,true)],PaymentMethod.Card);
        returns.Post(request);returns.Post(request with {OperationId=Guid.NewGuid(),RefundMethod=null});returns.Post(request with {OperationId=Guid.NewGuid(),RefundMethod=null});
        Assert.Equal(100,loyalty.Balance(customer));Assert.Equal(100,loyalty.History(customer).Where(e=>e.Kind=="reverse_redeem").Sum(e=>e.Points));
    }
    [Fact] public void Encrypted_restore_retains_promotions_point_allocations_and_return_history()
    {
        using var f=new PosFixture();using var dir=new TempFolder();var loyalty=new LoyaltyService(f.Db,f.Clock);var customer=f.Customers.Add(f.Owner,"Member");
        loyalty.Configure(f.Owner,new(true,1,10,1,0));loyalty.Adjust(Guid.NewGuid(),f.Owner,customer,100,"Opening");
        new PromotionService(f.Db,f.Clock).Save(Guid.NewGuid(),f.Owner,new(0,"Offer","عرض",PromotionKind.Percentage){Percent=10});
        var sale=f.Sales.Post(f.Sale(2,13,customer:customer) with {RedeemPoints=50});var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();
        new ReturnService(f.Db,f.Clock).Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Checked",[new(line.SaleLineId,1,true)],null));
        var package=new BackupService(f.Db,f.Clock).CreatePackage(dir.Combine("backup"),"loyalty-backup-secret",false);var path=dir.Combine("restored/pos.db");
        RestoreService.Restore(package,"loyalty-backup-secret",path,dir.Combine("preserved"),f.Clock,f.Owner);var restored=new PosDatabase(path);
        Assert.Equal(loyalty.Balance(customer),new LoyaltyService(restored,f.Clock).Balance(customer));Assert.Equal(loyalty.History(customer).Count,new LoyaltyService(restored,f.Clock).History(customer).Count);
        Assert.Single(new PromotionService(restored,f.Clock).List());Assert.Equal(100,new PromotionService(restored,f.Clock).ReturnedDiscountCents(sale.SaleId));
    }
}
