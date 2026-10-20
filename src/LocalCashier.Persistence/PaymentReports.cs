using LocalCashier.Domain;

namespace LocalCashier.Persistence;

public sealed record PaymentMethodTotals(long MethodId,string Code,string NameEn,string NameAr,string Kind,decimal Sales,decimal Collections,decimal Refunds,decimal SupplierPayments)
{public decimal Net=>Sales+Collections-Refunds-SupplierPayments;}

public sealed partial class ReportService
{
    public IReadOnlyList<PaymentMethodTotals> PaymentBreakdown(string fromDate,string toDate,long? cashierId=null,long? shiftId=null)
    {
        using var c=db.Open();
        return Sql.Query(c,"""
            WITH flows(method_id,sales,collections,refunds,supplier) AS (
              SELECT p.method_id,p.applied_cents,0,0,0 FROM payments p JOIN sales s ON s.id=p.sale_id
                WHERE s.status='posted' AND (@sh IS NOT NULL OR s.business_date BETWEEN @f AND @t) AND (@sh IS NULL OR s.shift_id=@sh) AND (@a IS NULL OR s.cashier_id=@a)
              UNION ALL
              SELECT p.method_id,0,p.amount_cents,0,0 FROM collections p JOIN shifts sh ON sh.id=p.shift_id
                WHERE (@sh IS NOT NULL OR sh.business_date BETWEEN @f AND @t) AND (@sh IS NULL OR p.shift_id=@sh) AND (@a IS NULL OR p.actor_id=@a)
              UNION ALL
              SELECT p.method_id,0,-p.amount_cents,0,0 FROM collection_reversals r JOIN collections p ON p.id=r.collection_id JOIN shifts sh ON sh.id=r.shift_id
                WHERE (@sh IS NOT NULL OR sh.business_date BETWEEN @f AND @t) AND (@sh IS NULL OR r.shift_id=@sh) AND (@a IS NULL OR r.actor_id=@a)
              UNION ALL
              SELECT p.method_id,0,0,p.amount_cents,0 FROM refund_payments p JOIN returns r ON r.id=p.return_id JOIN shifts sh ON sh.id=r.shift_id
                WHERE (@sh IS NOT NULL OR sh.business_date BETWEEN @f AND @t) AND (@sh IS NULL OR r.shift_id=@sh) AND (@a IS NULL OR r.actor_id=@a)
              UNION ALL
              SELECT p.method_id,0,0,0,p.amount_cents FROM supplier_payments p
                WHERE (@sh IS NOT NULL OR p.pay_date BETWEEN @f AND @t) AND (@sh IS NULL OR p.shift_id=@sh) AND (@a IS NULL OR p.actor_id=@a)
              UNION ALL
              SELECT p.method_id,0,0,0,-p.amount_cents FROM supplier_reversals r JOIN supplier_payments p ON r.target_type='payment' AND p.id=r.target_id
                WHERE (@sh IS NOT NULL OR substr(r.created_at,1,10) BETWEEN @f AND @t) AND (@sh IS NULL OR r.shift_id=@sh) AND (@a IS NULL OR r.actor_id=@a)
            )
            SELECT m.id,m.code,m.name_en,m.name_ar,m.kind,SUM(f.sales),SUM(f.collections),SUM(f.refunds),SUM(f.supplier)
            FROM flows f JOIN payment_methods m ON m.id=f.method_id GROUP BY m.id ORDER BY m.sort_order,m.id
            """,x=>new PaymentMethodTotals(x.GetInt64(0),x.GetString(1),x.GetString(2),x.GetString(3),x.GetString(4),Money.FromCents(x.GetInt64(5)),Money.FromCents(x.GetInt64(6)),Money.FromCents(x.GetInt64(7)),Money.FromCents(x.GetInt64(8))),
            ("@f",fromDate),("@t",toDate),("@a",cashierId),("@sh",shiftId));
    }
}
