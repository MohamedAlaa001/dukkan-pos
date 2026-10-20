using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed partial class LoyaltyService
{
    // Each earning line is an interval of integer points. Returns consume its prefix cumulatively.
    // Expiry marks disjoint intervals, so the same entitlement cannot be removed by both expiry and return.
    // Keeping the coordinate also handles a spent point restored after its original sale was returned.
    void RecordExpiredEarnings(SqliteConnection c,long credit,long expiry,long points)
    {
        long? sale=null;var visited=new HashSet<long>();
        while(visited.Add(credit))
        {
            if(Sql.Scalar<long?>(c,"SELECT parent_credit_id FROM loyalty_credit_origins WHERE credit_id=@i",("@i",credit)) is long parent){credit=parent;continue;}
            var origin=Sql.Query(c,"SELECT kind,source_id,sale_id FROM loyalty_ledger WHERE id=@i",x=>(Kind:x.GetString(0),Source:x.GetInt64(1),Sale:x.IsDBNull(2)?(long?)null:x.GetInt64(2)),("@i",credit)).Single();
            if(origin.Kind=="earn"){sale=origin.Sale;break;}
            if(origin.Kind!="reverse_redeem")return;
            credit=origin.Source;
        }
        if(sale is not long saleId)return;
        var lines=Sql.Query(c,"SELECT l.sale_line_id,l.earned_points FROM loyalty_sale_lines l JOIN sale_lines s ON s.id=l.sale_line_id WHERE s.sale_id=@s ORDER BY l.sale_line_id",
            x=>(Id:x.GetInt64(0),Earned:x.GetInt64(1)),("@s",saleId));
        var future=new List<(long Line,long Start,long End)>();var past=new List<(long Line,long Start,long End)>();
        foreach(var line in lines)
        {
            var returned=ReturnedEarned(c,line.Id);
            var ranges=Sql.Query(c,"SELECT start_points,end_points FROM loyalty_expired_earnings WHERE sale_line_id=@i ORDER BY start_points",
                x=>(Start:x.GetInt64(0),End:x.GetInt64(1)),("@i",line.Id));
            long cursor=0;
            void Free(long start,long end)
            {
                if(end<=start)return;
                if(end>returned)future.Add((line.Id,Math.Max(start,returned),end));
                if(start<returned)past.Add((line.Id,start,Math.Min(end,returned)));
            }
            foreach(var range in ranges){Free(cursor,range.Start);cursor=Math.Max(cursor,range.End);}
            Free(cursor,line.Earned);
        }
        var left=points;var affected=new HashSet<long>();
        foreach(var range in future.Concat(past))
        {
            var take=Math.Min(left,range.End-range.Start);if(take<=0)continue;
            Sql.Exec(c,"INSERT INTO loyalty_expired_earnings VALUES(@e,@l,@s,@t)",("@e",expiry),("@l",range.Line),("@s",range.Start),("@t",range.Start+take));
            affected.Add(range.Line);left-=take;if(left==0)break;
        }
        if(left!=0)throw new InvalidOperationException("Expired loyalty earning allocation exceeds its original entitlement.");
        foreach(var line in affected)ReconcileEarnReturn(c,saleId,line,$"expiry:{expiry}:reconcile:{line}",null,null);
    }

    static long ReturnedEarned(SqliteConnection c,long line)=>Sql.Scalar<long>(c,"SELECT COALESCE(SUM(p.earned_reversed),0) FROM loyalty_return_points p JOIN return_lines r ON r.id=p.return_line_id WHERE r.sale_line_id=@l",("@l",line));

    void ReconcileEarnReturn(SqliteConnection c,long sale,long line,string key,long? actor,long? returned)
    {
        var theoretical=ReturnedEarned(c,line);
        var exempt=Sql.Scalar<long>(c,"SELECT COALESCE(SUM(MAX(0,MIN(@t,end_points)-start_points)),0) FROM loyalty_expired_earnings WHERE sale_line_id=@l",("@t",theoretical),("@l",line));
        var actual=Sql.Scalar<long>(c,"SELECT -COALESCE(SUM(points),0) FROM loyalty_ledger WHERE sale_id=@s AND source_type='sale_line' AND source_id=@l AND kind='reverse_earn'",("@s",sale),("@l",line));
        var difference=theoretical-exempt-actual;if(difference==0)return;
        var customer=Sql.Scalar<long>(c,"SELECT customer_id FROM sales WHERE id=@s",("@s",sale));
        var originalCredit=Sql.Scalar<long?>(c,"SELECT id FROM loyalty_ledger WHERE sale_id=@s AND kind='earn'",("@s",sale));
        if(difference>0)Append(c,key,customer,"reverse_earn",-difference,"sale_line",line,actor,sale,returned,null,preferredCredit:originalCredit);
        else CompensateEarnDebit(c,key,customer,sale,line,-difference,actor,returned);
    }

    void CompensateEarnDebit(SqliteConnection c,string key,long customer,long sale,long line,long points,long? actor,long? returned)
    {
        var debits=Sql.Query(c,"""
            SELECT l.id,-l.points,COALESCE((SELECT SUM(a.points) FROM loyalty_allocations a WHERE a.debit_id=l.id),0),
              COALESCE((SELECT SUM(x.points) FROM loyalty_compensations x WHERE x.debit_id=l.id),0)
            FROM loyalty_ledger l WHERE l.sale_id=@s AND l.kind='reverse_earn' AND l.source_type='sale_line' AND l.source_id=@l AND l.points<0 ORDER BY l.id
            """,x=>(Id:x.GetInt64(0),Points:x.GetInt64(1),Funded:x.GetInt64(2),Compensated:x.GetInt64(3)),("@s",sale),("@l",line));
        var left=points;
        foreach(var debit in debits)
        {
            var available=debit.Points-debit.Compensated;if(available<=0)continue;
            var unfunded=Math.Min(left,Math.Min(available,debit.Points-debit.Funded));
            if(unfunded>0)
            {
                var credit=Append(c,$"{key}:debit:{debit.Id}:unfunded",customer,"reverse_earn",unfunded,"sale_line",line,actor,sale,returned,null,preferredDebit:debit.Id);
                Sql.Exec(c,"INSERT INTO loyalty_compensations VALUES(@c,@d,NULL,@p)",("@c",credit),("@d",debit.Id),("@p",unfunded));
                left-=unfunded;available-=unfunded;
            }
            if(left==0)break;
            var funding=Sql.Query(c,"""
                SELECT cr.id,cr.expires_on,a.points-COALESCE((SELECT SUM(x.points) FROM loyalty_compensations x WHERE x.debit_id=@d AND x.original_credit_id=cr.id),0)
                FROM loyalty_allocations a JOIN loyalty_ledger cr ON cr.id=a.credit_id WHERE a.debit_id=@d
                  AND cr.id NOT IN(SELECT credit_id FROM loyalty_compensations WHERE debit_id=@d AND original_credit_id IS NULL)
                ORDER BY cr.id
                """,x=>(Id:x.GetInt64(0),Expiry:x.IsDBNull(1)?null:x.GetString(1),Points:x.GetInt64(2)),("@d",debit.Id));
            foreach(var original in funding.Where(f=>f.Points>0))
            {
                var take=Math.Min(left,Math.Min(available,original.Points));if(take<=0)continue;
                var credit=Append(c,$"{key}:debit:{debit.Id}:credit:{original.Id}",customer,"reverse_earn",take,"sale_line",line,actor,sale,returned,original.Expiry);
                Sql.Exec(c,"INSERT INTO loyalty_credit_origins VALUES(@c,@p)",("@c",credit),("@p",original.Id));
                Sql.Exec(c,"INSERT INTO loyalty_compensations VALUES(@c,@d,@o,@p)",("@c",credit),("@d",debit.Id),("@o",original.Id),("@p",take));
                left-=take;available-=take;if(left==0)break;
            }
            if(left==0)break;
        }
        if(left!=0)throw new InvalidOperationException("Loyalty reversal compensation exceeds original debit funding.");
    }
}
