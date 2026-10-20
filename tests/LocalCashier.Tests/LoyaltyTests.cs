using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class LoyaltyTests
{
    sealed class Clock(DateTimeOffset now):IClock {public DateTimeOffset Now {get;set;}=now;}
    [Fact] public void Program_starts_disabled_and_owner_adjustments_are_audited_idempotent_and_permission_checked()
    {
        using var f=new PosFixture();var service=new LoyaltyService(f.Db,f.Clock);var customer=f.Customers.Add(f.Owner,"Member");
        Assert.False(service.Settings().Enabled);Assert.Equal(0,service.Balance(customer));
        Assert.Throws<PosException>(()=>service.Configure(f.Cashier,new(true,1,10,1,0)));
        Assert.Throws<PosException>(()=>service.Adjust(Guid.NewGuid(),f.Cashier,customer,10,"Opening"));
        Assert.Throws<PosException>(()=>service.Adjust(Guid.NewGuid(),f.Owner,customer,10,""));
        var operation=Guid.NewGuid();service.Adjust(operation,f.Owner,customer,10,"Opening");service.Adjust(operation,f.Owner,customer,10,"Opening");
        Assert.Throws<PosException>(()=>service.Adjust(operation,f.Cashier,customer,10,"Opening"));
        Assert.Equal(10,service.Balance(customer));Assert.Single(service.History(customer));
    }
    [Fact] public void Expiry_does_not_double_remove_points_used_to_cover_a_negative_adjustment()
    {
        using var f=new PosFixture();var clock=new Clock(f.Clock.Now);var service=new LoyaltyService(f.Db,clock);var customer=f.Customers.Add(f.Owner,"Member");
        service.Configure(f.Owner,new(true,1,10,1,1));service.Adjust(Guid.NewGuid(),f.Owner,customer,-5,"Previous return");
        service.Adjust(Guid.NewGuid(),f.Owner,customer,10,"Opening");Assert.Equal(5,service.Balance(customer));
        clock.Now=clock.Now.AddDays(2);Assert.Equal(0,service.Balance(customer));service.ExpireDue(customer);service.ExpireDue(customer);
        Assert.Single(service.History(customer).Where(e=>e.Kind=="expire"));Assert.Equal(0,service.History(customer).Sum(e=>e.Points));
    }
    [Fact] public void Earn_and_redemption_are_atomic_non_cash_and_retry_once()
    {
        using var f=new PosFixture();var service=new LoyaltyService(f.Db,f.Clock);var customer=f.Customers.Add(f.Owner,"Member");
        service.Configure(f.Owner,new(true,1.25m,10,5,0));service.Adjust(Guid.NewGuid(),f.Owner,customer,100,"Opening");
        var request=f.Sale(2,15,customer:customer) with {RedeemPoints=50};
        var posted=f.Sales.Post(request);Assert.True(f.Sales.Post(request).AlreadyPosted);Assert.Equal(0m,posted.Debt);Assert.Equal(0m,posted.Change);
        Assert.Equal(515m,f.Shifts.ExpectedCash(f.Shift));Assert.Equal(68,service.Balance(customer));
        var receipt=f.Sales.GetReceipt(posted.SaleId);Assert.Equal(5m,receipt.Payments.Single(p=>p.MethodCode=="loyalty").Applied);
        Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,0,customer:customer) with {RedeemPoints=1000}));
        Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,10) with {RedeemPoints=5}));
    }
    [Fact] public void Full_return_restores_redeemed_points_and_reverses_earned_points_without_cashout()
    {
        using var f=new PosFixture();var service=new LoyaltyService(f.Db,f.Clock);var customer=f.Customers.Add(f.Owner,"Member");
        service.Configure(f.Owner,new(true,1,10,1,0));service.Adjust(Guid.NewGuid(),f.Owner,customer,100,"Opening");
        var sale=f.Sales.Post(f.Sale(3,20,customer:customer) with {RedeemPoints=100});var line=f.Sales.FindForReturn(sale.Number)!.Lines.Single();var returns=new ReturnService(f.Db,f.Clock);
        for(var i=0;i<3;i++)returns.Post(new(Guid.NewGuid(),sale.SaleId,f.Shift,f.Cashier,f.Owner,"Partial",[new(line.SaleLineId,1,true)],PaymentMethod.Cash));
        Assert.Equal(100,service.Balance(customer));Assert.Equal(500m,f.Shifts.ExpectedCash(f.Shift));
        Assert.Equal(10m,new ReportService(f.Db).PaymentBreakdown(f.Clock.Now.ToString("yyyy-MM-dd"),f.Clock.Now.ToString("yyyy-MM-dd")).Single(p=>p.Code=="loyalty").Refunds);
    }
}
