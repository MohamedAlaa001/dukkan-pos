using System.Globalization;

namespace LocalCashier.Domain;

public enum PromotionKind { Percentage, FixedCart, BuyGet, Bundle }
public sealed record BundleRequirement(long ProductId,int Quantity);
public sealed record PromotionDefinition(long Id,string NameEn,string NameAr,PromotionKind Kind)
{
    public bool Enabled {get;init;}=true;
    public int Priority {get;init;}
    public bool Stackable {get;init;}
    public decimal Percent {get;init;}
    public long AmountCents {get;init;}
    public long MinimumCents {get;init;}
    public IReadOnlyList<long> ProductIds {get;init;}=[];
    public string? Category {get;init;}
    public string? CustomerGroup {get;init;}
    public string? Coupon {get;init;}
    public string? StartsOn {get;init;}
    public string? EndsOn {get;init;}
    public int Weekdays {get;init;}=127;
    public int StartMinute {get;init;}
    public int EndMinute {get;init;}
    public int BuyQuantity {get;init;}=2;
    public int GetQuantity {get;init;}=1;
    public bool SameProduct {get;init;}
    public IReadOnlyList<BundleRequirement> Bundle {get;init;}=[];
}
public sealed record PromotionContext(DateTimeOffset Now,string? Coupon=null,string? CustomerGroup=null);
public sealed record PromotionCartLine(long ProductId,string? Category,decimal Quantity,long BasisCents);
public sealed record PromotionAllocation(long PromotionId,int LineIndex,long AmountCents,string NameEn,string NameAr);
public sealed record PromotionEvaluation(IReadOnlyList<long> Discounts,IReadOnlyList<PromotionAllocation> Applied);

/// <summary>Pure price-basis discounts. Integer cents leave this boundary; no tax, payment or persistence side effects.</summary>
public static class PromotionEngine
{
    public static void Validate(PromotionDefinition rule)
    {
        static bool Date(string? value)=>string.IsNullOrWhiteSpace(value)||DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _);
        if(!Enum.IsDefined(rule.Kind)||string.IsNullOrWhiteSpace(rule.NameEn)||string.IsNullOrWhiteSpace(rule.NameAr)||rule.NameEn.Length>100||rule.NameAr.Length>100
           ||rule.Percent<0||rule.Percent>100||rule.Percent!=decimal.Round(rule.Percent,2)||rule.AmountCents<0||rule.MinimumCents<0
           ||rule.Kind!=PromotionKind.FixedCart&&rule.MinimumCents!=0
           ||rule.ProductIds.Any(i=>i<=0)||rule.ProductIds.Distinct().Count()!=rule.ProductIds.Count
           ||!Date(rule.StartsOn)||!Date(rule.EndsOn)||(!string.IsNullOrWhiteSpace(rule.StartsOn)&&!string.IsNullOrWhiteSpace(rule.EndsOn)&&string.CompareOrdinal(rule.StartsOn,rule.EndsOn)>0)
           ||rule.Weekdays<1||rule.Weekdays>127||rule.StartMinute<0||rule.StartMinute>=1440||rule.EndMinute<0||rule.EndMinute>=1440
           ||rule.Coupon?.Length>40||rule.CustomerGroup?.Length>80||rule.Category?.Length>100
           ||rule.Kind==PromotionKind.BuyGet&&(rule.BuyQuantity<=0||rule.GetQuantity<=0||rule.BuyQuantity>10000||rule.GetQuantity>10000)
           ||rule.Kind==PromotionKind.Bundle&&(rule.Bundle.Count==0||rule.Bundle.Any(b=>b.ProductId<=0||b.Quantity<=0||b.Quantity>10000)||rule.Bundle.Select(b=>b.ProductId).Distinct().Count()!=rule.Bundle.Count))
            throw new PosException(PosError.InvalidDiscount,"Invalid promotion rule.");
    }

    public static PromotionEvaluation Evaluate(IReadOnlyList<PromotionCartLine> lines,IReadOnlyList<PromotionDefinition> rules,PromotionContext context)
    {
        foreach(var line in lines)
        {
            if(line.ProductId<=0||line.Quantity<=0||line.BasisCents<0)throw new PosException(PosError.InvalidDiscount,"Invalid promotion cart basis.");
            Money.ToMilli(line.Quantity);
        }
        var remaining=lines.Select(l=>l.BasisCents).ToArray();var discounts=new long[lines.Count];var applied=new List<PromotionAllocation>();
        foreach(var rule in rules.OrderByDescending(r=>r.Priority).ThenBy(r=>r.Id))
        {
            Validate(rule);
            if(!Active(rule,context)||!rule.Stackable&&applied.Count>0)continue;
            var indexes=Enumerable.Range(0,lines.Count).Where(i=>Eligible(lines[i],rule)&&remaining[i]>0).ToArray();
            var weights=new decimal[lines.Count];long target=0;
            if(rule.Kind==PromotionKind.Percentage)
            {
                foreach(var i in indexes)weights[i]=remaining[i];
                target=Round(weights.Sum()*rule.Percent/100);
            }
            else if(rule.Kind==PromotionKind.FixedCart)
            {
                if(remaining.Sum()<rule.MinimumCents)continue;
                // Fixed cart offers apply to the cart; owner scopes belong to percentage/buy-get rules.
                for(var i=0;i<lines.Count;i++)weights[i]=remaining[i];
                target=Math.Min(rule.AmountCents,remaining.Sum());
            }
            else if(rule.Kind==PromotionKind.BuyGet)
            {
                var groups=rule.SameProduct?indexes.GroupBy(i=>lines[i].ProductId).Select(g=>g.ToArray()).ToArray():[indexes];
                foreach(var group in groups)
                {
                    // Aggregate quantities by product before dropping incomplete units, so splitting a cart row cannot change the offer.
                    var whole=group.GroupBy(i=>lines[i].ProductId).Sum(g=>(long)decimal.Floor(g.Sum(i=>lines[i].Quantity)));
                    var rewardMilli=checked(whole/(rule.BuyQuantity+rule.GetQuantity)*rule.GetQuantity*1000);
                    var caps=group.GroupBy(i=>lines[i].ProductId).ToDictionary(g=>g.Key,g=>(long)decimal.Floor(g.Sum(i=>lines[i].Quantity))*1000);
                    foreach(var i in group.OrderBy(i=>remaining[i]/lines[i].Quantity).ThenBy(i=>i))
                    {
                        var qty=Math.Min(Money.ToMilli(lines[i].Quantity),caps[lines[i].ProductId]);
                        caps[lines[i].ProductId]-=qty;var take=Math.Min(qty,rewardMilli);rewardMilli-=take;
                        weights[i]+=(decimal)remaining[i]*take/Money.ToMilli(lines[i].Quantity)*rule.Percent/100;
                        if(rewardMilli==0)break;
                    }
                }
                target=Round(weights.Sum());
            }
            else
            {
                var eligible=indexes.ToHashSet();
                var sets=rule.Bundle.Min(b=>(long)decimal.Floor(indexes.Where(i=>lines[i].ProductId==b.ProductId).Sum(i=>lines[i].Quantity)/b.Quantity));
                if(sets==0)continue;
                foreach(var b in rule.Bundle)
                {
                    var needed=checked(sets*b.Quantity*1000);
                    // Cheapest current units participate first; cart row order only breaks economically equal ties.
                    foreach(var i in indexes.Where(i=>eligible.Contains(i)&&lines[i].ProductId==b.ProductId).OrderBy(i=>remaining[i]/lines[i].Quantity).ThenBy(i=>i))
                    {
                        var take=Math.Min(needed,Money.ToMilli(lines[i].Quantity));needed-=take;
                        weights[i]=(decimal)remaining[i]*take/Money.ToMilli(lines[i].Quantity);
                        if(needed==0)break;
                    }
                }
                target=Math.Max(0,Round(weights.Sum())-checked(rule.AmountCents*sets));
            }
            if(target<=0)continue;
            var shares=Allocate(target,weights,remaining);
            for(var i=0;i<shares.Length;i++)if(shares[i]>0)
            {
                remaining[i]-=shares[i];discounts[i]+=shares[i];applied.Add(new(rule.Id,i,shares[i],rule.NameEn,rule.NameAr));
            }
            if(!rule.Stackable&&shares.Sum()>0)break;
        }
        return new(discounts,applied);
    }

    static bool Eligible(PromotionCartLine line,PromotionDefinition rule)=>(rule.ProductIds.Count==0||rule.ProductIds.Contains(line.ProductId))
        &&(string.IsNullOrWhiteSpace(rule.Category)||string.Equals(rule.Category,line.Category,StringComparison.OrdinalIgnoreCase));

    static bool Active(PromotionDefinition r,PromotionContext c)
    {
        if(!r.Enabled||!string.IsNullOrWhiteSpace(r.Coupon)&&!string.Equals(r.Coupon.Trim(),c.Coupon?.Trim(),StringComparison.OrdinalIgnoreCase)
            ||!string.IsNullOrWhiteSpace(r.CustomerGroup)&&!string.Equals(r.CustomerGroup.Trim(),c.CustomerGroup?.Trim(),StringComparison.OrdinalIgnoreCase))return false;
        var date=DateOnly.FromDateTime(c.Now.DateTime);var minute=c.Now.Hour*60+c.Now.Minute;
        if(r.StartMinute!=r.EndMinute)
        {
            if(r.StartMinute<r.EndMinute){if(minute<r.StartMinute||minute>=r.EndMinute)return false;}
            else {if(minute>=r.EndMinute&&minute<r.StartMinute)return false;if(minute<r.EndMinute)date=date.AddDays(-1);}
        }
        var stamp=date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        return (r.Weekdays&(1<<(int)date.DayOfWeek))!=0&&(string.IsNullOrWhiteSpace(r.StartsOn)||string.CompareOrdinal(stamp,r.StartsOn)>=0)
            &&(string.IsNullOrWhiteSpace(r.EndsOn)||string.CompareOrdinal(stamp,r.EndsOn)<=0);
    }

    static long Round(decimal cents)=>checked((long)decimal.Round(cents,0,MidpointRounding.AwayFromZero));

    static long[] Allocate(long target,decimal[] weights,long[] capacities)
    {
        var shares=new long[weights.Length];var sum=weights.Sum();if(sum<=0)return shares;
        target=Math.Min(target,capacities.Where((_,i)=>weights[i]>0).Sum());var fractions=new decimal[weights.Length];
        for(var i=0;i<weights.Length;i++)
        {
            var exact=target*weights[i]/sum;shares[i]=Math.Min(capacities[i],(long)decimal.Floor(exact));fractions[i]=exact-decimal.Floor(exact);
        }
        var remainder=target-shares.Sum();
        // Uncapped weighted amounts normally leave fewer than n pennies; handle capped lines without losing a penny.
        while(remainder>0)
        {
            var progress=false;
            foreach(var i in Enumerable.Range(0,weights.Length).Where(i=>weights[i]>0&&shares[i]<capacities[i]).OrderByDescending(i=>fractions[i]).ThenBy(i=>i))
            {shares[i]++;remainder--;progress=true;if(remainder==0)break;}
            if(!progress)break;
        }
        return shares;
    }
}
