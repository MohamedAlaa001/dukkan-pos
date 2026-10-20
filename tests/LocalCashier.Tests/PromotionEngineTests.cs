using LocalCashier.Domain;

namespace LocalCashier.Tests;

public class PromotionEngineTests
{
    [Fact] public void Bundle_duplicate_product_prices_do_not_change_when_cart_rows_are_reordered()
    {
        var rule=Rule(1,PromotionKind.Bundle) with {AmountCents=500,Bundle=[new(1,1),new(2,1)]};
        var first=PromotionEngine.Evaluate([Line(1,1,10000),Line(1,1,1000),Line(2,1,1000)],[rule],Context);
        var reordered=PromotionEngine.Evaluate([Line(1,1,1000),Line(1,1,10000),Line(2,1,1000)],[rule],Context);
        Assert.Equal(first.Discounts.Sum(),reordered.Discounts.Sum());Assert.Equal(1500,first.Discounts.Sum());
    }
    static readonly PromotionContext Context=new(new DateTimeOffset(2026,10,19,12,0,0,TimeSpan.FromHours(3)));
    static PromotionDefinition Rule(long id,PromotionKind kind)=>new(id,"Offer "+id,"عرض "+id,kind);
    static PromotionCartLine Line(long id,decimal qty,long cents,string category="Food")=>new(id,category,qty,cents);
    [Fact] public void Percentage_rounds_eligible_total_once_and_allocates_every_piastre()
    {
        var rule=Rule(1,PromotionKind.Percentage) with {Percent=50};
        var result=PromotionEngine.Evaluate([Line(1,1,1),Line(2,1,1),Line(3,1,1)],[rule],Context);
        Assert.Equal(new long[]{1,1,0},result.Discounts);Assert.Equal(2,result.Applied.Sum(a=>a.AmountCents));
        Assert.Equal(2,PromotionEngine.Evaluate([Line(1,3,3)],[rule],Context).Discounts.Sum());
    }
    [Fact] public void Product_category_and_group_scope_and_case_insensitive_coupon_are_enforced()
    {
        var rule=Rule(1,PromotionKind.Percentage) with {Percent=10,Category="Food",ProductIds=[1],CustomerGroup="VIP",Coupon="SAVE"};
        var lines=new[]{Line(1,1,1000),Line(2,1,1000),Line(1,1,1000,"Other")};
        Assert.Equal(0,PromotionEngine.Evaluate(lines,[rule],Context).Discounts.Sum());
        var result=PromotionEngine.Evaluate(lines,[rule],Context with {Coupon=" save ",CustomerGroup="VIP"});
        Assert.Equal(new long[]{100,0,0},result.Discounts);
    }
    [Fact] public void Fixed_cart_threshold_uses_remaining_basis_and_caps_at_zero()
    {
        var rule=Rule(1,PromotionKind.FixedCart) with {AmountCents=700,MinimumCents=500};
        Assert.Equal(0,PromotionEngine.Evaluate([Line(1,1,499)],[rule],Context).Discounts.Sum());
        Assert.Equal(500,PromotionEngine.Evaluate([Line(1,1,500)],[rule],Context).Discounts.Sum());
    }
    [Fact] public void Priority_and_nonstackable_rules_have_deterministic_order()
    {
        var high=Rule(3,PromotionKind.Percentage) with {Percent=10,Priority=10,Stackable=true};
        var lower=Rule(1,PromotionKind.Percentage) with {Percent=50,Stackable=true};
        var blocked=Rule(2,PromotionKind.FixedCart) with {AmountCents=900,Priority=5};
        var result=PromotionEngine.Evaluate([Line(1,1,1000)],[lower,blocked,high],Context);
        Assert.Equal(550,result.Discounts.Sum());Assert.Equal(new long[]{3,1},result.Applied.Select(a=>a.PromotionId));
        Assert.Equal(900,PromotionEngine.Evaluate([Line(1,1,1000)],[lower,blocked with {Priority=20},high],Context).Discounts.Sum());
    }
    [Fact] public void Date_and_weekday_overnight_window_use_the_injected_clock()
    {
        var rule=Rule(1,PromotionKind.Percentage) with {Percent=10,StartsOn="2026-10-19",EndsOn="2026-10-20",Weekdays=2,StartMinute=22*60,EndMinute=2*60};
        Assert.Equal(0,PromotionEngine.Evaluate([Line(1,1,1000)],[rule],Context).Discounts.Sum());
        Assert.Equal(100,PromotionEngine.Evaluate([Line(1,1,1000)],[rule],Context with {Now=Context.Now.AddHours(11)}).Discounts.Sum());
        Assert.Equal(100,PromotionEngine.Evaluate([Line(1,1,1000)],[rule],Context with {Now=Context.Now.AddHours(13)}).Discounts.Sum());
        Assert.Equal(0,PromotionEngine.Evaluate([Line(1,1,1000)],[rule],Context with {Now=Context.Now.AddHours(14)}).Discounts.Sum());
    }
    [Fact] public void Buy_two_get_one_chooses_cheapest_or_same_product_and_handles_split_lines()
    {
        var rule=Rule(1,PromotionKind.BuyGet) with {BuyQuantity=2,GetQuantity=1,Percent=100};
        var lines=new[]{Line(1,1,200),Line(2,1,100),Line(3,1,300)};
        Assert.Equal(new long[]{0,100,0},PromotionEngine.Evaluate(lines,[rule],Context).Discounts);
        Assert.Equal(0,PromotionEngine.Evaluate(lines,[rule with {SameProduct=true}],Context).Discounts.Sum());
        Assert.Equal(100,PromotionEngine.Evaluate([Line(1,0.5m,50),Line(1,2.5m,250)],[rule with {SameProduct=true}],Context).Discounts.Sum());
        Assert.Equal(0,PromotionEngine.Evaluate([Line(1,2.999m,300)],[rule],Context).Discounts.Sum());
        Assert.Equal(50,PromotionEngine.Evaluate(lines,[rule with {Percent=50}],Context).Discounts.Sum());
    }
    [Fact] public void Bundle_repeats_only_complete_sets_and_never_increases_a_price()
    {
        var rule=Rule(1,PromotionKind.Bundle) with {AmountCents=250,Bundle=[new(1,2),new(2,1)]};
        Assert.Equal(100,PromotionEngine.Evaluate([Line(1,5,500),Line(2,2,200)],[rule],Context).Discounts.Sum());
        Assert.Equal(0,PromotionEngine.Evaluate([Line(1,1,100),Line(2,1,100)],[rule],Context).Discounts.Sum());
        Assert.Equal(0,PromotionEngine.Evaluate([Line(1,2,100),Line(2,1,100)],[rule],Context).Discounts.Sum());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Automatic_discount_reduces_tax_basis_and_preserves_manual_discount(bool inclusive)
    {
        var doc=PricingEngine.Price([new(1,inclusive?125.4m:110m,Discount.Fixed(inclusive?11.4m:10m),0.14m)],null,inclusive);
        var result=PricingEngine.ApplyAdditionalDiscounts(doc,[inclusive?1140L:1000L],inclusive);
        Assert.Equal(90m,result.Taxable);Assert.Equal(12.6m,result.Tax);Assert.Equal(102.6m,result.Total);
        Assert.Equal(inclusive?22.8m:20m,result.Discount);
    }
    [Fact] public void Invalid_rules_and_negative_inputs_are_refused()
    {
        Assert.Throws<PosException>(()=>PromotionEngine.Evaluate([Line(1,1,100)],[Rule(1,PromotionKind.Percentage) with {Percent=101}],Context));
        Assert.Throws<PosException>(()=>PromotionEngine.Evaluate([Line(1,1,-1)],[],Context));
        Assert.Throws<PosException>(()=>PromotionEngine.Evaluate([Line(1,1,100)],[Rule(1,PromotionKind.Bundle) with {Bundle=[new(1,0)]}],Context));
    }
}
