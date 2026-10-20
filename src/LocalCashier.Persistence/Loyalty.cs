using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record LoyaltyConfig(bool Enabled,decimal EarnRate,long ValueCents,long MinimumRedeem,int ExpiryDays);
public sealed record LoyaltyEntry(long Id,string Kind,long Points,string CreatedAt,string? ExpiresOn,long? SaleId,long? ReturnId);

/// <summary>Immutable integer points with FIFO funding allocations; balances are always derived.</summary>
public sealed partial class LoyaltyService(PosDatabase db,IClock clock)
{
    public LoyaltyConfig Settings(){using var c=db.Open();return Config(c);}
    internal static LoyaltyConfig Config(SqliteConnection c)=>Sql.Query(c,"SELECT enabled,earn_rate_milli,value_cents,minimum_points,expiry_days FROM loyalty_config WHERE id=1",
        x=>new LoyaltyConfig(x.GetInt64(0)==1,x.GetInt64(1)/1000m,x.GetInt64(2),x.GetInt64(3),x.GetInt32(4))).Single();
    public void Configure(long ownerId,LoyaltyConfig config)
    {
        using var c=db.Open();Db.InOwnerTransaction(c,()=>
        {
            if(config.EarnRate<0||config.EarnRate>1000000||config.EarnRate!=decimal.Round(config.EarnRate,3)||config.ValueCents<1||config.ValueCents>100000000
                ||config.MinimumRedeem<1||config.ExpiryDays<0||config.ExpiryDays>36500)throw new PosException(PosError.InvalidPrice,"Invalid loyalty settings.");
            Sql.Exec(c,"UPDATE loyalty_config SET enabled=@e,earn_rate_milli=@r,value_cents=@v,minimum_points=@m,expiry_days=@d WHERE id=1",
                ("@e",config.Enabled?1:0),("@r",(long)(config.EarnRate*1000)),("@v",config.ValueCents),("@m",config.MinimumRedeem),("@d",config.ExpiryDays));
            Common.Audit(c,clock,ownerId,null,"loyalty.configure","1");return 0;
        },ownerId);
    }
    static void RequireCustomer(SqliteConnection c,long customer)
    {
        if(Sql.Scalar<long>(c,"SELECT COUNT(*) FROM customers WHERE id=@i AND active=1",("@i",customer))!=1)throw new PosException(PosError.NotFound,"Active customer not found.");
    }
    public long Balance(long customer){using var c=db.Open();return Balance(c,customer);}
    internal long Balance(SqliteConnection c,long customer)=>Sql.Scalar<long>(c,"""
        SELECT COALESCE((SELECT SUM(points) FROM loyalty_ledger WHERE customer_id=@c),0)-COALESCE((
          SELECT SUM(l.points-COALESCE((SELECT SUM(a.points) FROM loyalty_allocations a WHERE a.credit_id=l.id),0))
          FROM loyalty_ledger l WHERE l.customer_id=@c AND l.points>0 AND l.expires_on IS NOT NULL AND l.expires_on<=@d),0)
        """,("@c",customer),("@d",Common.Today(clock)));
    public IReadOnlyList<LoyaltyEntry> History(long customer)
    {
        using var c=db.Open();return Sql.Query(c,"SELECT id,kind,points,created_at,expires_on,sale_id,return_id FROM loyalty_ledger WHERE customer_id=@c ORDER BY id DESC",
            x=>new LoyaltyEntry(x.GetInt64(0),x.GetString(1),x.GetInt64(2),x.GetString(3),x.IsDBNull(4)?null:x.GetString(4),x.IsDBNull(5)?null:x.GetInt64(5),x.IsDBNull(6)?null:x.GetInt64(6)),("@c",customer));
    }
    public void Adjust(Guid operationId,long ownerId,long customer,long points,string reason)
    {
        using var c=db.Open();Db.InOwnerTransaction(c,()=>
        {
            var key="adjust:"+operationId.ToString("N");if(Sql.Scalar<long>(c,"SELECT COUNT(*) FROM loyalty_ledger WHERE operation_key=@k",("@k",key))>0)return 0;
            RequireCustomer(c,customer);
            if(points==0||points==long.MinValue||Math.Abs(points)>1000000000||string.IsNullOrWhiteSpace(reason))throw new PosException(PosError.InvalidPrice,"A nonzero point adjustment and reason are required.");
            Expire(c,customer);Append(c,key,customer,"adjust",points,"customer",customer,ownerId,null,null,points>0?Expiry(Config(c)):null);
            Common.Audit(c,clock,ownerId,null,"loyalty.adjust",customer.ToString(),reason,$"points={points}");return 0;
        },ownerId);
    }
    string? Expiry(LoyaltyConfig config)=>config.ExpiryDays==0?null:DateOnly.Parse(Common.Today(clock)).AddDays(config.ExpiryDays).ToString("yyyy-MM-dd");
    public void ExpireDue(long customer){using var c=db.Open();Db.InTransaction(c,()=>{Expire(c,customer);return 0;});}
    void Expire(SqliteConnection c,long customer)
    {
        while(true)
        {
        var expired=Sql.Query(c,"""
            SELECT l.id,l.points-COALESCE((SELECT SUM(a.points) FROM loyalty_allocations a WHERE a.credit_id=l.id),0)
            FROM loyalty_ledger l WHERE l.customer_id=@c AND l.points>0 AND l.expires_on IS NOT NULL AND l.expires_on<=@d
            """,x=>(Id:x.GetInt64(0),Points:x.GetInt64(1)),("@c",customer),("@d",Common.Today(clock)));
        var due=expired.Where(e=>e.Points>0).ToList();if(due.Count==0)break;
        foreach(var row in due)
        {
            var expiry=Append(c,"expire:"+row.Id,customer,"expire",-row.Points,"loyalty_credit",row.Id,null,null,null,null,row.Id);
            RecordExpiredEarnings(c,row.Id,expiry,row.Points);
        }
        }
    }
    long Append(SqliteConnection c,string key,long customer,string kind,long points,string source,long sourceId,long? actor,long? sale,long? returned,string? expires,long? expireCredit=null,long? preferredCredit=null,long? preferredDebit=null)
    {
        var id=Sql.Insert(c,"""
            INSERT INTO loyalty_ledger(operation_key,customer_id,kind,points,source_type,source_id,actor_id,sale_id,return_id,created_at,expires_on)
              VALUES(@k,@c,@t,@p,@s,@i,@a,@sale,@return,@at,@exp)
            """,("@k",key),("@c",customer),("@t",kind),("@p",points),("@s",source),("@i",sourceId),("@a",actor),("@sale",sale),("@return",returned),("@at",Common.Stamp(clock)),("@exp",expires));
        if(expireCredit is long credit){Pair(c,id,credit,-points);return id;}
        var positive=points>0;
        if(positive&&expires is not null&&string.CompareOrdinal(expires,Common.Today(clock))<=0)return id;
        var candidates=Sql.Query(c,positive?"""
            SELECT l.id,-l.points-COALESCE((SELECT SUM(a.points) FROM loyalty_allocations a WHERE a.debit_id=l.id),0)
            FROM loyalty_ledger l WHERE l.customer_id=@c AND l.points<0 ORDER BY CASE WHEN l.id=@debit THEN 0 ELSE 1 END,l.id
            """:"""
            SELECT l.id,l.points-COALESCE((SELECT SUM(a.points) FROM loyalty_allocations a WHERE a.credit_id=l.id),0)
            FROM loyalty_ledger l WHERE l.customer_id=@c AND l.points>0 AND (l.expires_on IS NULL OR l.expires_on>@d)
            ORDER BY CASE WHEN l.id=@preferred THEN 0 ELSE 1 END,COALESCE(l.expires_on,'9999-12-31'),l.id
            """,x=>(Id:x.GetInt64(0),Points:x.GetInt64(1)),("@c",customer),("@d",Common.Today(clock)),("@preferred",preferredCredit),("@debit",preferredDebit));
        var remaining=Math.Abs(points);
        foreach(var row in candidates.Where(e=>e.Points>0))
        {
            var take=Math.Min(row.Points,remaining);Pair(c,positive?row.Id:id,positive?id:row.Id,take);remaining-=take;if(remaining==0)break;
        }
        return id;
    }
    static void Pair(SqliteConnection c,long debit,long credit,long points)=>Sql.Exec(c,"INSERT INTO loyalty_allocations VALUES(@d,@c,@p)",("@d",debit),("@c",credit),("@p",points));

    internal long ValidateRedemption(SqliteConnection c,long? customer,long points,long total)
    {
        if(points<0)throw new PosException(PosError.PaymentMismatch,"Points cannot be negative.");if(points==0)return 0;
        var config=Config(c);
        if(!config.Enabled||customer is not long member||points<config.MinimumRedeem)throw new PosException(PosError.PaymentMismatch,"Loyalty redemption is unavailable or below its minimum.");
        RequireCustomer(c,member);Expire(c,member);
        if(points>Balance(c,member)||points>total/config.ValueCents)throw new PosException(PosError.PaymentMismatch,"Insufficient points or redemption exceeds the sale.");
        return checked(points*config.ValueCents);
    }

    internal void RecordSale(SqliteConnection c,long sale,long? customer,long actor,long redeemed,long redemption, PricedDocument doc,IReadOnlyList<long> lineIds)
    {
        var config=Config(c);if(customer is not long member||!config.Enabled)return;
        RequireCustomer(c,member);Expire(c,member);
        if(redeemed>0)
        {
            Append(c,"sale:"+sale+":redeem",member,"redeem",-redeemed,"sale",sale,actor,sale,null,null);
            var payment=Sql.Insert(c,"INSERT INTO payments(sale_id,method,applied_cents,tendered_cents) VALUES(@s,'loyalty',@v,@v)",("@s",sale),("@v",redemption));
            PaymentMethodService.Snapshot(c,"payments",payment,PaymentMethodService.Get(c,10),null);
        }
        var earned=checked((long)decimal.Floor((Money.ToCents(doc.Total)-redemption)*config.EarnRate/100));
        if(earned>0)Append(c,"sale:"+sale+":earn",member,"earn",earned,"sale",sale,actor,sale,null,Expiry(config));
        var shares=PricingEngine.Allocate(earned,doc.Lines.Select(l=>Money.ToCents(l.Total)).ToArray());
        for(var i=0;i<lineIds.Count;i++)Sql.Exec(c,"INSERT INTO loyalty_sale_lines VALUES(@l,@p)",("@l",lineIds[i]),("@p",shares[i]));
        Sql.Exec(c,"UPDATE sales SET earned_points=@e,redeemed_points=@r,redemption_cents=@v,loyalty_balance_after=@b,loyalty_active=1 WHERE id=@s",
            ("@e",earned),("@r",redeemed),("@v",redemption),("@b",Balance(c,member)),("@s",sale));
    }

    internal void RecordReturnLine(SqliteConnection c,long sale,long returned,long returnLine,long saleLine,decimal quantity,long actor)
    {
        var earned=Sql.Scalar<long>(c,"SELECT earned_points FROM loyalty_sale_lines WHERE sale_line_id=@i",("@i",saleLine));if(earned==0)return;
        var sold=Sql.Scalar<long>(c,"SELECT qty_milli FROM sale_lines WHERE id=@i",("@i",saleLine));
        var prior=Sql.Query(c,"SELECT r.qty_milli,COALESCE(p.earned_reversed,0) FROM return_lines r LEFT JOIN loyalty_return_points p ON p.return_line_id=r.id WHERE r.sale_line_id=@s AND r.id<>@i",
            x=>(Quantity:x.GetInt64(0),Points:x.GetInt64(1)),("@s",saleLine),("@i",returnLine));
        var remaining=earned-prior.Sum(p=>p.Points);var qty=Money.ToMilli(quantity);
        var cumulative=(long)decimal.Round((decimal)earned*(qty+prior.Sum(p=>p.Quantity))/sold,0,MidpointRounding.AwayFromZero);
        var reverse=Math.Min(remaining,Math.Max(0,cumulative-prior.Sum(p=>p.Points)));
        Sql.Exec(c,"INSERT INTO loyalty_return_points VALUES(@r,@p)",("@r",returnLine),("@p",reverse));
        var customer=Sql.Scalar<long>(c,"SELECT customer_id FROM sales WHERE id=@i",("@i",sale));
        Expire(c,customer);
        ReconcileEarnReturn(c,sale,saleLine,"returnline:"+returnLine+":earn",actor,returned);
    }

    internal void RestoreRedemption(SqliteConnection c,long sale,long returned,long actor)
    {
        var info=Sql.Query(c,"SELECT customer_id,redeemed_points,redemption_cents FROM sales WHERE id=@i",x=>(Customer:x.IsDBNull(0)?0:x.GetInt64(0),Points:x.GetInt64(1),Value:x.GetInt64(2)),("@i",sale)).Single();
        if(info.Points==0)return;
        var refunded=Sql.Scalar<long>(c,"SELECT COALESCE(SUM(p.amount_cents),0) FROM refund_payments p JOIN returns r ON r.id=p.return_id WHERE r.sale_id=@s AND p.method_id=10",("@s",sale));
        var target=refunded>=info.Value?info.Points:(long)decimal.Floor((decimal)info.Points*refunded/info.Value);
        var restored=Sql.Scalar<long>(c,"SELECT COALESCE(SUM(points),0) FROM loyalty_ledger WHERE sale_id=@s AND kind='reverse_redeem'",("@s",sale));
        var needed=target-restored;if(needed<=0)return;
        var originals=Sql.Query(c,"""
            SELECT cr.id,a.points,cr.expires_on FROM loyalty_ledger d JOIN loyalty_allocations a ON a.debit_id=d.id JOIN loyalty_ledger cr ON cr.id=a.credit_id
            WHERE d.sale_id=@s AND d.kind='redeem' ORDER BY COALESCE(cr.expires_on,'9999-12-31'),cr.id
            """,x=>(Id:x.GetInt64(0),Points:x.GetInt64(1),Expiry:x.IsDBNull(2)?null:x.GetString(2)),("@s",sale));
        foreach(var original in originals)
        {
            var previous=Sql.Scalar<long>(c,"SELECT COALESCE(SUM(points),0) FROM loyalty_ledger WHERE sale_id=@s AND kind='reverse_redeem' AND source_id=@i",("@s",sale),("@i",original.Id));
            var take=Math.Min(needed,original.Points-previous);if(take<=0)continue;
            Append(c,$"return:{returned}:restore:{original.Id}",info.Customer,"reverse_redeem",take,"loyalty_credit",original.Id,actor,sale,returned,original.Expiry);needed-=take;if(needed==0)break;
        }
        if(needed!=0)throw new PosException(PosError.PaymentMismatch,"Original point funding is incomplete.");
        Expire(c,info.Customer);
    }
}
