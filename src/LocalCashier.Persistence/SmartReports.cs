using System.Globalization;
using LocalCashier.Domain;

namespace LocalCashier.Persistence;

public enum ProfitGroup { Product,Category,Cashier,Day,Month,TaxRate }
public sealed record ProfitRow(string Key,string NameEn,string? NameAr,decimal Quantity,decimal Revenue,decimal Cost,decimal Discounts,decimal Returns,decimal Tax)
{
    public decimal Profit=>Revenue-Cost;
    public decimal Margin=>Revenue==0?0:decimal.Round(Profit/Revenue*100,2);
}
public sealed record StockValueRow(long ProductId,string NameEn,string? NameAr,string Category,decimal Quantity,decimal CostValue,decimal RetailValue,string? LastSale);
public sealed record TaxReportRow(decimal Rate,decimal Taxable,decimal Tax);
public sealed record DailySummaryReport(string Date,long? CashierId,decimal Sales,decimal Returns,decimal Revenue,decimal Tax,decimal Expenses,
    decimal OpeningCash,decimal ExpectedCash,decimal? CountedCash,int OpenShifts,IReadOnlyList<PaymentMethodTotals> Methods,IReadOnlyList<ProfitRow> TopItems);

public sealed partial class ReportService
{
    static void ReportRange(string from,string to)
    {
        if(!DateOnly.TryParseExact(from,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var f)
           ||!DateOnly.TryParseExact(to,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var t)||f>t)
            throw new ArgumentException("Invalid report date range.");
    }
    /// <summary>Posting-period profit. Return actor attribution matches the dashboard. Classification uses today's catalog; amounts always use posted snapshots.</summary>
    public IReadOnlyList<ProfitRow> Profit(string from,string to,ProfitGroup group,long? cashierId=null)
    {
        ReportRange(from,to);using var c=db.Open();
        var products=Sql.Query(c,"SELECT p.id,p.name,p.name_alt,COALESCE(st.category,p.category,''),p.sku FROM products p LEFT JOIN styles st ON st.id=p.style_id",
            x=>(Id:x.GetInt64(0),Name:x.GetString(1),Alt:x.IsDBNull(2)?null:x.GetString(2),Cat:x.GetString(3),Sku:x.GetString(4))).ToDictionary(p=>p.Id);
        var users=Sql.Query(c,"SELECT id,display_name FROM users",x=>(Id:x.GetInt64(0),Name:x.GetString(1))).ToDictionary(u=>u.Id,u=>u.Name);
        var key=group switch{ProfitGroup.Product or ProfitGroup.Category=>"CAST(l.product_id AS TEXT)",ProfitGroup.Cashier=>"CAST(s.cashier_id AS TEXT)",ProfitGroup.Day=>"s.business_date",ProfitGroup.Month=>"substr(s.business_date,1,7)",ProfitGroup.TaxRate=>"CAST(l.tax_rate_bp AS TEXT)",_=>throw new ArgumentOutOfRangeException(nameof(group))};
        (string Key,string Name,string? Alt) Identity(string k)
        {
            if(group is ProfitGroup.Product or ProfitGroup.Category)
            {var p=products[long.Parse(k,CultureInfo.InvariantCulture)];return group==ProfitGroup.Category?(p.Cat,p.Cat,null):(k,p.Name+" · "+p.Sku,string.IsNullOrWhiteSpace(p.Alt)?null:p.Alt+" · "+p.Sku);}
            return group==ProfitGroup.Cashier?(k,users[long.Parse(k,CultureInfo.InvariantCulture)],null):(k,k,null);
        }
        var args=new (string,object?)[]{("@f",from),("@t",to),("@a",cashierId)};
        var rows=Sql.Query(c,$"""
            SELECT {key},SUM(l.qty_milli),SUM(l.taxable_cents),SUM(l.cogs_cents),SUM(l.line_discount_cents+l.order_discount_cents),SUM(l.tax_cents)
            FROM sales s JOIN sale_lines l ON l.sale_id=s.id WHERE s.status='posted' AND s.business_date BETWEEN @f AND @t AND (@a IS NULL OR s.cashier_id=@a) GROUP BY {key}
            """,x=>{var id=Identity(x.GetString(0));return new ProfitRow(id.Key,id.Name,id.Alt,Money.FromMilli(x.GetInt64(1)),Money.FromCents(x.GetInt64(2)),Money.FromCents(x.GetInt64(3)),Money.FromCents(x.GetInt64(4)),0,Money.FromCents(x.GetInt64(5)));},args).ToList();
        var returned=Sql.Query(c,"""
            SELECT l.product_id,r.actor_id,sh.business_date,l.tax_rate_bp,rl.qty_milli,rl.taxable_cents,rl.tax_cents,rl.cogs_reversed_cents,rl.total_cents,
              l.qty_milli,l.line_discount_cents+l.order_discount_cents,
              COALESCE((SELECT SUM(prev.qty_milli) FROM return_lines prev WHERE prev.sale_line_id=rl.sale_line_id AND prev.id<rl.id),0),
              COALESCE((SELECT SUM(ap.amount_cents) FROM applied_promotions ap WHERE ap.sale_line_id=l.id),0),
              COALESCE((SELECT SUM(rp.amount_cents) FROM return_promotion_allocations rp WHERE rp.return_line_id=rl.id),0)
            FROM returns r JOIN shifts sh ON sh.id=r.shift_id JOIN return_lines rl ON rl.return_id=r.id JOIN sale_lines l ON l.id=rl.sale_line_id
            WHERE sh.business_date BETWEEN @f AND @t AND (@a IS NULL OR r.actor_id=@a)
            """,x=>
            {
                var k=group switch{ProfitGroup.Product or ProfitGroup.Category=>x.GetInt64(0).ToString(CultureInfo.InvariantCulture),ProfitGroup.Cashier=>x.GetInt64(1).ToString(CultureInfo.InvariantCulture),ProfitGroup.Day=>x.GetString(2),ProfitGroup.Month=>x.GetString(2)[..7],_=>x.GetInt64(3).ToString(CultureInfo.InvariantCulture)};
                var id=Identity(k);var qty=x.GetInt64(4);var original=x.GetInt64(9);var disc=x.GetInt64(10)-x.GetInt64(12);var prior=x.GetInt64(11);
                long Alloc(long q)=>(long)decimal.Round((decimal)disc*q/original,0,MidpointRounding.AwayFromZero);
                return new ProfitRow(id.Key,id.Name,id.Alt,-Money.FromMilli(qty),-Money.FromCents(x.GetInt64(5)),-Money.FromCents(x.GetInt64(7)),-Money.FromCents(Alloc(prior+qty)-Alloc(prior)+x.GetInt64(13)),Money.FromCents(x.GetInt64(8)),-Money.FromCents(x.GetInt64(6)));
            },args);
        rows.AddRange(returned);
        return rows.GroupBy(r=>r.Key).Select(g=>new ProfitRow(g.Key,g.First().NameEn,g.First().NameAr,g.Sum(r=>r.Quantity),g.Sum(r=>r.Revenue),g.Sum(r=>r.Cost),g.Sum(r=>r.Discounts),g.Sum(r=>r.Returns),g.Sum(r=>r.Tax))).OrderByDescending(r=>r.Revenue).ThenBy(r=>r.Key,StringComparer.Ordinal).ToList();
    }
    public IReadOnlyList<TaxReportRow> TaxSummary(string from,string to,long? cashierId=null)=>Profit(from,to,ProfitGroup.TaxRate,cashierId)
        .Select(r=>new TaxReportRow(decimal.Parse(r.Key,CultureInfo.InvariantCulture)/100,r.Revenue,r.Tax)).OrderBy(r=>r.Rate).ToList();

    public IReadOnlyList<StockValueRow> StockValuation()=>StockRows(null,null);
    IReadOnlyList<StockValueRow> StockRows(string? asOf,string? cutoff)
    {
        if(asOf is not null)ReportRange(asOf,asOf);using var c=db.Open();
        var last=cutoff is null?"NULL":"(SELECT MAX(s.business_date) FROM sale_lines l JOIN sales s ON s.id=l.sale_id WHERE l.product_id=p.id AND s.status='posted' AND s.business_date<=@d)";
        var dead=cutoff is null?"":"AND b.qty>0 AND NOT EXISTS(SELECT 1 FROM sale_lines l JOIN sales s ON s.id=l.sale_id WHERE l.product_id=p.id AND s.status='posted' AND s.business_date BETWEEN @cut AND @d)";
        return Sql.Query(c,$"""
            WITH balances AS (SELECT product_id,SUM(qty_milli) qty FROM stock_movements GROUP BY product_id)
            SELECT p.id,p.name,p.name_alt,COALESCE(st.category,p.category,''),b.qty,p.avg_cost_x10000,p.price_cents,{last},p.sku
            FROM products p JOIN balances b ON b.product_id=p.id LEFT JOIN styles st ON st.id=p.style_id
            WHERE b.qty<>0 {dead} ORDER BY p.name,p.id
            """,x=>{var qty=Money.FromMilli(x.GetInt64(4));return new StockValueRow(x.GetInt64(0),x.GetString(1)+" · "+x.GetString(8),x.IsDBNull(2)?null:x.GetString(2)+" · "+x.GetString(8),x.GetString(3),qty,
                Money.FromCents(Money.ToCents(qty*Money.FromCost4(x.GetInt64(5)))),Money.FromCents(Money.ToCents(qty*Money.FromCents(x.GetInt64(6)))),x.IsDBNull(7)?null:x.GetString(7));},("@d",asOf),("@cut",cutoff));
    }
    public IReadOnlyList<StockValueRow> DeadStock(string asOf,int days)
    {
        ReportRange(asOf,asOf);if(days<1||days>36500)throw new ArgumentOutOfRangeException(nameof(days));
        var cutoff=DateOnly.ParseExact(asOf,"yyyy-MM-dd",CultureInfo.InvariantCulture).AddDays(1-days).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        return StockRows(asOf,cutoff).OrderByDescending(r=>r.CostValue).ToList();
    }
    public DailySummaryReport DailySummary(string date,long? cashierId=null)
    {
        ReportRange(date,date);using var c=db.Open();var args=new (string,object?)[]{("@d",date),("@a",cashierId)};
        var sales=Sql.Scalar<long>(c,"SELECT COALESCE(SUM(total_cents),0) FROM sales WHERE status='posted' AND business_date=@d AND (@a IS NULL OR cashier_id=@a)",args);
        var ret=Sql.Scalar<long>(c,"SELECT COALESCE(SUM(r.total_cents),0) FROM returns r JOIN shifts sh ON sh.id=r.shift_id WHERE sh.business_date=@d AND (@a IS NULL OR r.actor_id=@a)",args);
        var expense=Sql.Scalar<long>(c,"SELECT COALESCE(SUM(amount_cents),0) FROM expenses WHERE expense_date=@d AND (@a IS NULL OR actor_id=@a)",args);
        var shifts=Sql.Query(c,"SELECT id,opening_float_cents,closing_count_cents,status FROM shifts WHERE business_date=@d AND (@a IS NULL OR cashier_id=@a)",
            x=>(Id:x.GetInt64(0),Opening:x.GetInt64(1),Count:x.IsDBNull(2)?(long?)null:x.GetInt64(2),Open:x.GetString(3)=="open"),args);
        var profit=Profit(date,date,ProfitGroup.Product,cashierId);
        return new(date,cashierId,Money.FromCents(sales),Money.FromCents(ret),profit.Sum(r=>r.Revenue),profit.Sum(r=>r.Tax),Money.FromCents(expense),Money.FromCents(shifts.Sum(s=>s.Opening)),
            Money.FromCents(shifts.Sum(s=>Common.ExpectedCashCents(c,s.Id))),shifts.Count==0||shifts.Any(s=>s.Count is null)?null:Money.FromCents(shifts.Sum(s=>s.Count!.Value)),shifts.Count(s=>s.Open),PaymentBreakdown(date,date,cashierId),profit.Where(r=>r.Quantity>0).OrderByDescending(r=>r.Quantity).Take(5).ToList());
    }
}
