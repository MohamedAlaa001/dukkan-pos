using System.Text.Json;
using LocalCashier.Application;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record PromotionQuote(PricedDocument Document,PromotionEvaluation Evaluation);
public sealed record AppliedPromotionRow(long Id,long SaleLineId,long PromotionId,long AmountCents,string NameEn,string NameAr);

public sealed class PromotionService(PosDatabase db,IClock clock)
{
    public IReadOnlyList<PromotionDefinition> List(){using var c=db.Open();return Load(c);}
    internal static IReadOnlyList<PromotionDefinition> Load(SqliteConnection c)=>Sql.Query(c,
        "SELECT r.definition_json FROM promotion_rules r JOIN promotions p ON p.id=r.promotion_id ORDER BY p.priority DESC,p.id",
        x=>JsonSerializer.Deserialize<PromotionDefinition>(x.GetString(0))!);

    public long Save(Guid operationId,long ownerId,PromotionDefinition rule)
    {
        using var c=db.Open();return Db.InOwnerTransaction(c,()=>
        {
            var prior=Sql.Scalar<long?>(c,"SELECT target_id FROM promotion_operations WHERE operation_id=@o AND kind='save'",("@o",operationId.ToString("N")));
            if(prior is long saved)return saved;
            PromotionEngine.Validate(rule);
            foreach(var product in rule.ProductIds.Concat(rule.Bundle.Select(b=>b.ProductId)).Distinct())
                if(Sql.Scalar<long>(c,"SELECT COUNT(*) FROM products WHERE id=@i",("@i",product))!=1)throw new PosException(PosError.NotFound,"Promotion product not found.");
            long id;
            if(rule.Id==0)id=Sql.Insert(c,"INSERT INTO promotions(name_en,name_ar,enabled,priority) VALUES(@e,@a,@on,@p)",("@e",rule.NameEn.Trim()),("@a",rule.NameAr.Trim()),("@on",rule.Enabled?1:0),("@p",rule.Priority));
            else
            {
                id=rule.Id;
                if(Sql.Exec(c,"UPDATE promotions SET name_en=@e,name_ar=@a,enabled=@on,priority=@p WHERE id=@i",("@e",rule.NameEn.Trim()),("@a",rule.NameAr.Trim()),("@on",rule.Enabled?1:0),("@p",rule.Priority),("@i",id))!=1)
                    throw new PosException(PosError.NotFound,"Promotion not found.");
            }
            var stored=rule with {Id=id,NameEn=rule.NameEn.Trim(),NameAr=rule.NameAr.Trim(),Coupon=rule.Coupon?.Trim(),CustomerGroup=rule.CustomerGroup?.Trim(),Category=rule.Category?.Trim()};
            Sql.Exec(c,"INSERT INTO promotion_rules(promotion_id,definition_json) VALUES(@i,@j) ON CONFLICT(promotion_id) DO UPDATE SET definition_json=excluded.definition_json",("@i",id),("@j",JsonSerializer.Serialize(stored)));
            Sql.Exec(c,"INSERT INTO promotion_operations VALUES(@o,'save',@i)",("@o",operationId.ToString("N")),("@i",id));
            Common.Audit(c,clock,ownerId,null,"promotion.configure",id.ToString(),null,$"kind={rule.Kind};enabled={rule.Enabled}");return id;
        },ownerId);
    }

    public void SetCustomerGroup(Guid operationId,long ownerId,long customerId,string group)
    {
        using var c=db.Open();Db.InOwnerTransaction(c,()=>
        {
            if(Sql.Scalar<long>(c,"SELECT COUNT(*) FROM promotion_operations WHERE operation_id=@o",("@o",operationId.ToString("N")))>0)return 0;
            if(group.Trim().Length>80)throw new PosException(PosError.InvalidPrice,"Customer group is too long.");
            if(Sql.Exec(c,"UPDATE customers SET loyalty_group=@g WHERE id=@i",("@g",group.Trim()),("@i",customerId))!=1)throw new PosException(PosError.NotFound,"Customer not found.");
            Sql.Exec(c,"INSERT INTO promotion_operations VALUES(@o,'group',@i)",("@o",operationId.ToString("N")),("@i",customerId));
            Common.Audit(c,clock,ownerId,null,"customer.group",customerId.ToString(),null,group.Trim());return 0;
        },ownerId);
    }
    public string CustomerGroup(long customerId){using var c=db.Open();return Sql.Scalar<string>(c,"SELECT loyalty_group FROM customers WHERE id=@i",("@i",customerId))??"";}

    public PromotionQuote Preview(IReadOnlyList<SaleLineRequest> lines,Discount? orderDiscount,long? customerId=null,string? coupon=null,IReadOnlyList<long>? suppressed=null)
    {
        using var c=db.Open();var mode=Sql.Scalar<string>(c,"SELECT value FROM settings WHERE key='tax_price_mode'")??throw new PosException(PosError.TaxNotConfigured,"Tax is not configured.");
        var inputs=lines.Select(line=>
        {
            var rows=Sql.Query(c,"SELECT price_cents,tax_rate_bp FROM products WHERE id=@i AND active=1",x=>(Price:Money.FromCents(x.GetInt64(0)),Tax:x.GetInt64(1)/10000m),("@i",line.ProductId));
            if(rows.Count==0)throw new PosException(PosError.ProductUnavailable,"Product is unavailable for sale.");var row=rows[0];
            return new PricingLineInput(line.Quantity,line.PriceOverride??row.Price,line.Discount??Discount.None,row.Tax);
        }).ToList();
        return Quote(c,PricingEngine.Price(inputs,orderDiscount,mode=="inclusive"),lines.Select(l=>l.ProductId).ToList(),customerId,coupon,suppressed,mode=="inclusive");
    }
    internal PromotionQuote Quote(SqliteConnection c,PricedDocument original,IReadOnlyList<long> productIds,long? customerId,string? coupon,IReadOnlyList<long>? suppressed,bool inclusive)
    {
        if(coupon?.Length>40)throw new PosException(PosError.InvalidDiscount,"Coupon is too long.");
        var group=customerId is long customer?Sql.Scalar<string>(c,"SELECT loyalty_group FROM customers WHERE id=@i",("@i",customer)):null;
        var lines=original.Lines.Select((line,i)=>new PromotionCartLine(productIds[i],Sql.Scalar<string>(c,"SELECT COALESCE(s.category,p.category) FROM products p LEFT JOIN styles s ON s.id=p.style_id WHERE p.id=@i",("@i",productIds[i])),line.Quantity,Money.ToCents(line.Gross-line.LineDiscount-line.OrderDiscount))).ToList();
        var evaluation=PromotionEngine.Evaluate(lines,Load(c).Where(r=>suppressed is null||!suppressed.Contains(r.Id)).ToList(),new(clock.Now,coupon,group));
        return new(PricingEngine.ApplyAdditionalDiscounts(original,evaluation.Discounts,inclusive),evaluation);
    }
    public IReadOnlyList<AppliedPromotionRow> Applied(long saleId)
    {
        using var c=db.Open();return Sql.Query(c,"SELECT id,sale_line_id,promotion_id,amount_cents,name_en,name_ar FROM applied_promotions WHERE sale_id=@i ORDER BY id",
            x=>new AppliedPromotionRow(x.GetInt64(0),x.GetInt64(1),x.GetInt64(2),x.GetInt64(3),x.GetString(4),x.GetString(5)),("@i",saleId));
    }
    public long ReturnedDiscountCents(long saleId)
    {
        using var c=db.Open();return Sql.Scalar<long>(c,"SELECT COALESCE(SUM(r.amount_cents),0) FROM return_promotion_allocations r JOIN applied_promotions p ON p.id=r.applied_id WHERE p.sale_id=@i",("@i",saleId));
    }
    internal static void RecordReturn(SqliteConnection c,long saleLineId,long returnLineId,decimal quantity)
    {
        var sold=Money.FromMilli(Sql.Scalar<long>(c,"SELECT qty_milli FROM sale_lines WHERE id=@i",("@i",saleLineId)));
        var priorQty=Money.FromMilli(Sql.Scalar<long>(c,"SELECT COALESCE(SUM(qty_milli),0) FROM return_lines WHERE sale_line_id=@s AND id<>@i",("@s",saleLineId),("@i",returnLineId)));
        foreach(var row in Sql.Query(c,"SELECT id,amount_cents FROM applied_promotions WHERE sale_line_id=@i",x=>(Id:x.GetInt64(0),Cents:x.GetInt64(1)),("@i",saleLineId)))
        {
            var prior=Sql.Scalar<long>(c,"SELECT COALESCE(SUM(amount_cents),0) FROM return_promotion_allocations WHERE applied_id=@i",("@i",row.Id));
            var amount=ReturnCalculator.Calculate(sold,Money.FromCents(row.Cents),0,priorQty,Money.FromCents(prior),0,quantity);
            Sql.Exec(c,"INSERT INTO return_promotion_allocations VALUES(@r,@p,@c)",("@r",returnLineId),("@p",row.Id),("@c",Money.ToCents(amount.Total)));
        }
    }
}
