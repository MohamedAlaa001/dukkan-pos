using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record RefundPaymentRow(long MethodId,string Code,string Kind,string NameEn,string NameAr,decimal Amount);
public sealed partial class ReturnService
{
    public IReadOnlyList<RefundPaymentRow> RefundPayments(long returnId)
    {
        using var c=db.Open();return Sql.Query(c,"SELECT p.method_id,m.code,p.kind,p.method_name_en,p.method_name_ar,p.amount_cents FROM refund_payments p JOIN payment_methods m ON m.id=p.method_id WHERE p.return_id=@r ORDER BY p.id",
            x=>new RefundPaymentRow(x.GetInt64(0),x.GetString(1),x.GetString(2),x.GetString(3),x.GetString(4),Money.FromCents(x.GetInt64(5))),("@r",returnId));
    }
    sealed record PaidMethod(long Id,string Code,string Kind,string En,string Ar,long Cents);
    static IReadOnlyList<RefundPaymentRow> PlanRefund(SqliteConnection c,long saleId,long refund,long? selected)
    {
        if(refund<=0)return [];
        var originals=Sql.Query(c,"""
            SELECT p.method_id,m.code,m.kind,COALESCE(p.method_name_en,m.name_en),COALESCE(p.method_name_ar,m.name_ar),p.applied_cents
            FROM payments p JOIN payment_methods m ON m.id=p.method_id WHERE p.sale_id=@s AND p.applied_cents>0
            UNION ALL
            SELECT col.method_id,m.code,m.kind,COALESCE(col.method_name_en,m.name_en),COALESCE(col.method_name_ar,m.name_ar),a.amount_cents
            FROM collection_allocations a JOIN collections col ON col.id=a.collection_id JOIN payment_methods m ON m.id=col.method_id
            WHERE a.sale_id=@s AND col.id NOT IN(SELECT collection_id FROM collection_reversals)
            """,x=>new PaidMethod(x.GetInt64(0),x.GetString(1),x.GetString(2),x.GetString(3),x.GetString(4),x.GetInt64(5)),("@s",saleId))
            .GroupBy(p=>p.Id).Select(g=>g.First() with{Cents=g.Sum(p=>p.Cents)}).OrderBy(p=>p.Id).ToList();
        var prior=Sql.Query(c,"SELECT p.method_id,SUM(p.amount_cents) FROM refund_payments p JOIN returns r ON r.id=p.return_id WHERE r.sale_id=@s GROUP BY p.method_id",
            x=>(Id:x.GetInt64(0),Cents:x.GetInt64(1)),("@s",saleId)).ToDictionary(r=>r.Id,r=>r.Cents);
        var remaining=originals.Select(p=>p with{Cents=Math.Max(0,p.Cents-prior.GetValueOrDefault(p.Id))}).Where(p=>p.Cents>0).ToList();
        var pointMethod=remaining.FirstOrDefault(p=>p.Code=="loyalty");
        long pointRefund=0;
        if(pointMethod is not null)
        {
            // Owner money-method overrides cannot cash out points. Keep their share tied to actual remaining paid value,
            // including when a previous override used a method outside its original allocation.
            var actualRemaining=originals.Sum(p=>p.Cents)-prior.Values.Sum();
            if(actualRemaining<refund||actualRemaining<pointMethod.Cents)throw new PosException(PosError.PaymentMismatch,"Refund exceeds actual paid value.");
            pointRefund=PricingEngine.Allocate(refund,[actualRemaining-pointMethod.Cents,pointMethod.Cents])[1];
            remaining.Remove(pointMethod);
        }
        var moneyRefund=refund-pointRefund;
        var pointRows=pointRefund>0?new[]{new RefundPaymentRow(pointMethod!.Id,pointMethod.Code,pointMethod.Kind,pointMethod.En,pointMethod.Ar,Money.FromCents(pointRefund))}:Array.Empty<RefundPaymentRow>();
        if(selected is long methodId)
        {
            var method=PaymentMethodService.Get(c,methodId);
            if(method.Kind=="credit" || !method.Enabled&&!originals.Any(p=>p.Id==methodId))throw new PosException(PosError.PaymentMismatch,"Refund method is unavailable.");
            var original=originals.FirstOrDefault(p=>p.Id==methodId);
            return moneyRefund==0?pointRows:[..pointRows,new(method.Id,method.Code,method.Kind,original?.En??method.NameEn,original?.Ar??method.NameAr,Money.FromCents(moneyRefund))];
        }
        if(moneyRefund==0)return pointRows;
        var capacity=remaining.Sum(p=>p.Cents);
        if(capacity<moneyRefund)throw new PosException(PosError.PaymentMismatch,"Refund exceeds remaining original payments.");
        var parts=remaining.Select(p=>(Method:p,Exact:(decimal)moneyRefund*p.Cents/capacity)).ToList();
        var amounts=parts.ToDictionary(p=>p.Method.Id,p=>(long)decimal.Truncate(p.Exact));
        var pennies=moneyRefund-amounts.Values.Sum();
        foreach(var p in parts.OrderByDescending(p=>p.Exact-decimal.Truncate(p.Exact)).ThenBy(p=>p.Method.Id).Take((int)pennies))amounts[p.Method.Id]++;
        return [..pointRows,..remaining.Where(p=>amounts[p.Id]>0).Select(p=>new RefundPaymentRow(p.Id,p.Code,p.Kind,p.En,p.Ar,Money.FromCents(amounts[p.Id])))];
    }
}
