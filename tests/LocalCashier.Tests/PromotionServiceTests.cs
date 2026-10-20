using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class PromotionServiceTests
{
    [Fact] public void Preview_refuses_an_unavailable_product_instead_of_showing_a_zero_price()
    {
        using var f=new PosFixture();Assert.Throws<PosException>(()=>new PromotionService(f.Db,f.Clock).Preview([new(long.MaxValue,1)],null));
    }
    [Fact] public void Checkout_refuses_a_stale_expected_total_when_rules_or_loyalty_value_change()
    {
        using var f=new PosFixture();var service=new PromotionService(f.Db,f.Clock);
        service.Save(Guid.NewGuid(),f.Owner,new(0,"Offer","عرض",PromotionKind.Percentage){Percent=10});
        Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,10) with {ExpectedTotal=10}));Assert.Equal(0,f.Count("sales"));
        var customer=f.Customers.Add(f.Owner,"Member");var loyalty=new LoyaltyService(f.Db,f.Clock);loyalty.Configure(f.Owner,new(true,1,10,1,0));loyalty.Adjust(Guid.NewGuid(),f.Owner,customer,100,"Opening");
        Assert.Throws<PosException>(()=>f.Sales.Post(f.Sale(1,4,customer:customer) with {ExpectedTotal=9,RedeemPoints=50,ExpectedRedemptionValue=4}));
        Assert.Equal(100,loyalty.Balance(customer));Assert.Equal(0,f.Count("sales"));
    }
    [Fact] public void Owner_rules_apply_automatically_and_retry_or_rule_edits_do_not_reprice_history()
    {
        using var f=new PosFixture();var service=new PromotionService(f.Db,f.Clock);var operation=Guid.NewGuid();
        var rule=new PromotionDefinition(0,"Ten percent","خصم عشرة بالمئة",PromotionKind.Percentage){Percent=10};
        Assert.Throws<PosException>(()=>service.Save(operation,f.Cashier,rule));
        var id=service.Save(operation,f.Owner,rule);Assert.Equal(id,service.Save(operation,f.Owner,rule));
        Assert.Throws<PosException>(()=>service.Save(operation,f.Cashier,rule));
        var request=f.Sale(2,20);var posted=f.Sales.Post(request);Assert.Equal(18m,posted.Total);Assert.Equal(2m,posted.Change);
        Assert.True(f.Sales.Post(request).AlreadyPosted);Assert.Equal(200,service.Applied(posted.SaleId).Sum(a=>a.AmountCents));
        service.Save(Guid.NewGuid(),f.Owner,rule with {Id=id,Percent=50,NameEn="Edited"});
        Assert.Equal(18m,f.Sales.GetReceipt(posted.SaleId).Total);Assert.Equal("Ten percent",service.Applied(posted.SaleId)[0].NameEn);
    }
    [Fact] public void Cashier_cannot_suppress_an_offer_without_named_owner_and_reason()
    {
        using var f=new PosFixture();var service=new PromotionService(f.Db,f.Clock);
        var id=service.Save(Guid.NewGuid(),f.Owner,new(0,"Offer","عرض",PromotionKind.Percentage){Percent=10});
        var request=f.Sale(1,10) with {SuppressedPromotionIds=[id]};
        Assert.Throws<PosException>(()=>f.Sales.Post(request));
        Assert.Throws<PosException>(()=>f.Sales.Post(request with {ApproverId=f.Owner}));
        Assert.Equal(10m,f.Sales.Post(request with {ApproverId=f.Owner,PromotionOverrideReason="Customer exception"}).Total);
    }
    [Fact] public void Partial_returns_reverse_saved_offer_piastre_allocations_and_full_return_is_exact()
    {
        using var f=new PosFixture();var service=new PromotionService(f.Db,f.Clock);
        service.Save(Guid.NewGuid(),f.Owner,new(0,"Bundle","باقة",PromotionKind.Bundle){AmountCents=2000,Bundle=[new(f.Widget,3)]});
        var posted=f.Sales.Post(f.Sale(3,30));Assert.Equal(20m,posted.Total);
        var saleLine=f.Sales.FindForReturn(posted.Number)!.Lines.Single();var returns=new ReturnService(f.Db,f.Clock);
        for(var i=0;i<3;i++)returns.Post(new(Guid.NewGuid(),posted.SaleId,f.Shift,f.Cashier,f.Owner,"Return",[new(saleLine.SaleLineId,1,true)],null));
        Assert.Equal(1000,service.ReturnedDiscountCents(posted.SaleId));Assert.Equal(500m,f.Shifts.ExpectedCash(f.Shift));
    }
    [Fact] public void Preview_uses_customer_group_coupon_and_same_pricing_as_post()
    {
        using var f=new PosFixture();var service=new PromotionService(f.Db,f.Clock);var customer=f.Customers.Add(f.Owner,"VIP");
        service.SetCustomerGroup(Guid.NewGuid(),f.Owner,customer,"VIP");
        Assert.Throws<PosException>(()=>service.SetCustomerGroup(Guid.NewGuid(),f.Cashier,customer,"VIP"));
        service.Save(Guid.NewGuid(),f.Owner,new(0,"Member","عميل مميز",PromotionKind.Percentage){Percent=10,CustomerGroup="VIP",Coupon="VIP10"});
        var request=f.Sale(2,20,customer:customer) with {Coupon="vip10"};
        var preview=service.Preview(request.Lines,request.OrderDiscount,customer,request.Coupon);
        Assert.Equal(18m,preview.Document.Total);Assert.Equal(preview.Document.Total,f.Sales.Post(request).Total);
    }
}
