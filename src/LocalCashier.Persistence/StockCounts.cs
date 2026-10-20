using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record CountSessionRow(long Id, long LocationId, string Location, string State, string CreatedAt) { public override string ToString() => $"#{Id} · {Location}"; }
public sealed record StockCountLineRow(long Id, long ProductId, string Product, long? LotId, string? Batch, decimal Expected, decimal? Counted, decimal UnitCost)
{
    public decimal Variance => Counted is decimal qty ? qty - Expected : 0;
    public decimal VarianceValue => Money.Round(Variance * UnitCost);
}

public sealed partial class StockControlService
{
    public long CreateCount(Guid operationId, long ownerId, long locationId, IReadOnlyList<long>? products = null)
    {
        using var c = db.Open();
        return Db.InOwnerTransaction(c, () =>
        {
            if (Sql.Scalar<long?>(c, "SELECT id FROM count_sessions WHERE operation_id=@o", ("@o", operationId.ToString("N"))) is long old) return old;
            StockTracking.RequireLocation(c, locationId);
            var ids = products?.Distinct().ToList() ?? Sql.Query(c, "SELECT id FROM products WHERE active=1 ORDER BY id", x => x.GetInt64(0));
            if (ids.Count == 0) throw new PosException(PosError.EmptyCart, "Choose products to count.");
            foreach(var product in ids) StockTracking.RequireNoOpenCount(c,product,locationId);
            var cursor = Sql.Scalar<long>(c, "SELECT COALESCE(MAX(id),0) FROM stock_movements");
            var id = Sql.Insert(c, "INSERT INTO count_sessions(operation_id,location_id,movement_cursor,actor_id,created_at) VALUES(@o,@l,@m,@a,@t)",
                ("@o", operationId.ToString("N")), ("@l", locationId), ("@m", cursor), ("@a", ownerId), ("@t", Common.Stamp(clock)));
            foreach (var product in ids)
            {
                StockTracking.RequireProduct(c, product);
                var buckets = StockTracking.Buckets(c, product, locationId);
                if (buckets.Count == 0) buckets.Add(new(null, null, null, 0));
                var cost = Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", product));
                foreach (var b in buckets) Sql.Exec(c, "INSERT INTO count_lines(session_id,product_id,lot_id,expected_milli,cost_x10000) VALUES(@s,@p,@lot,@q,@c)",
                    ("@s", id), ("@p", product), ("@lot", b.LotId), ("@q", b.Qty), ("@c", cost));
            }
            Common.Audit(c, clock, ownerId, null, "stock.count_open", id.ToString()); return id;
        }, ownerId);
    }
    public IReadOnlyList<CountSessionRow> CountSessions()
    {
        using var c = db.Open();
        return Sql.Query(c, "SELECT s.id,s.location_id,l.name,s.state,s.created_at FROM count_sessions s JOIN stock_locations l ON l.id=s.location_id ORDER BY s.id DESC",
            x => new CountSessionRow(x.GetInt64(0), x.GetInt64(1), x.GetString(2), x.GetString(3), x.GetString(4)));
    }
    public void CancelCount(Guid operationId,long ownerId,long sessionId,string reason)
    {
        using var c=db.Open();
        Db.InOwnerTransaction(c,()=>
        {
            if(Sql.Scalar<long?>(c,"SELECT session_id FROM count_cancellations WHERE operation_id=@o",("@o",operationId.ToString("N"))) is long old)
            {if(old!=sessionId)throw new PosException(PosError.Duplicate,"Operation already used.");return 0;}
            if(string.IsNullOrWhiteSpace(reason) || Sql.Scalar<string>(c,"SELECT state FROM count_sessions WHERE id=@s",("@s",sessionId))!="open")
                throw new PosException(PosError.InvalidQuantity,"An open count and cancellation reason are required.");
            Sql.Exec(c,"INSERT INTO count_cancellations(operation_id,session_id,reason,actor_id,created_at) VALUES(@o,@s,@r,@a,@t)",
                ("@o",operationId.ToString("N")),("@s",sessionId),("@r",reason.Trim()),("@a",ownerId),("@t",Common.Stamp(clock)));
            Sql.Exec(c,"UPDATE count_sessions SET state='cancelled' WHERE id=@s",("@s",sessionId));
            Common.Audit(c,clock,ownerId,null,"stock.count_cancel",sessionId.ToString(),reason.Trim());return 0;
        },ownerId);
    }
    static List<StockCountLineRow> CountLines(SqliteConnection c, long sessionId) => Sql.Query(c, """
        SELECT line.id,line.product_id,p.name,line.lot_id,lot.batch,line.cost_x10000,e.counted_milli,
          line.expected_milli+COALESCE((SELECT SUM(m.qty_milli) FROM stock_movements m
             WHERE m.product_id=line.product_id AND m.location_id=s.location_id AND m.lot_id IS line.lot_id
             AND m.id>s.movement_cursor AND m.id<=COALESCE(e.movement_cursor,(SELECT COALESCE(MAX(id),0) FROM stock_movements))),0)
        FROM count_lines line JOIN count_sessions s ON s.id=line.session_id JOIN products p ON p.id=line.product_id
        LEFT JOIN stock_lots lot ON lot.id=line.lot_id LEFT JOIN count_entries e ON e.id=(SELECT MAX(id) FROM count_entries WHERE line_id=line.id)
        WHERE line.session_id=@s ORDER BY line.id
        """, x => new StockCountLineRow(x.GetInt64(0), x.GetInt64(1), x.GetString(2), x.IsDBNull(3) ? null : x.GetInt64(3), x.IsDBNull(4) ? null : x.GetString(4),
            Money.FromMilli(x.GetInt64(7)), x.IsDBNull(6) ? null : Money.FromMilli(x.GetInt64(6)), Money.FromCost4(x.GetInt64(5))), ("@s", sessionId));
    public IReadOnlyList<StockCountLineRow> CountLines(long sessionId) { using var c = db.Open(); return CountLines(c, sessionId); }
    public void RecordCount(Guid operationId, long actorId, long lineId, decimal counted)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireActive(c, actorId);
            var old = Sql.Scalar<long?>(c, "SELECT line_id FROM count_entries WHERE operation_id=@o", ("@o", operationId.ToString("N")));
            if (old is long existing) { if (existing != lineId) throw new PosException(PosError.Duplicate, "Operation already used."); return 0; }
            var qty = Money.ToMilli(counted);
            if (qty < 0) throw new PosException(PosError.InvalidQuantity, "Count cannot be negative.");
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM count_lines l JOIN count_sessions s ON s.id=l.session_id WHERE l.id=@i AND s.state='open'", ("@i", lineId)) != 1)
                throw new PosException(PosError.PermissionDenied, "Count session is not open.");
            Sql.Exec(c, "INSERT INTO count_entries(operation_id,line_id,counted_milli,movement_cursor,actor_id,created_at) VALUES(@o,@l,@q,@m,@a,@t)",
                ("@o", operationId.ToString("N")), ("@l", lineId), ("@q", qty), ("@m", Sql.Scalar<long>(c, "SELECT COALESCE(MAX(id),0) FROM stock_movements")), ("@a", actorId), ("@t", Common.Stamp(clock)));
            Common.Audit(c, clock, actorId, null, "stock.count_entry", lineId.ToString(), null, $"quantity={counted}"); return 0;
        });
    }
    public long ApproveCount(Guid operationId, long ownerId, long sessionId, string reason)
    {
        using var c = db.Open();
        return Db.InOwnerTransaction(c, () =>
        {
            var old = Sql.Query(c, "SELECT id,session_id FROM count_approvals WHERE operation_id=@o", x => (Id: x.GetInt64(0), Session: x.GetInt64(1)), ("@o", operationId.ToString("N"))).FirstOrDefault();
            if (old.Id > 0) { if (old.Session != sessionId) throw new PosException(PosError.Duplicate, "Operation already used."); return old.Id; }
            var session = Sql.Query(c, "SELECT location_id,state FROM count_sessions WHERE id=@i", x => (Location: x.GetInt64(0), State: x.GetString(1)), ("@i", sessionId)).FirstOrDefault();
            if (session.State != "open" || string.IsNullOrWhiteSpace(reason)) throw new PosException(PosError.PermissionDenied, "An open session and approval reason are required.");
            var lines = CountLines(c, sessionId);
            if (lines.Count == 0 || lines.Any(l => l.Counted is null)) throw new PosException(PosError.InvalidQuantity, "Count every selected line before approval.");
            var stamp = Common.Stamp(clock);
            var document = Sql.Insert(c, "INSERT INTO count_approvals(operation_id,session_id,reason,actor_id,created_at) VALUES(@o,@s,@r,@a,@t)",
                ("@o", operationId.ToString("N")), ("@s", sessionId), ("@r", reason.Trim()), ("@a", ownerId), ("@t", stamp));
            foreach (var line in lines)
            {
                var delta = Money.ToMilli(line.Variance);
                if (delta == 0) continue;
                if (StockTracking.Tracked(c, line.ProductId) && line.LotId is null) throw new PosException(PosError.InvalidQuantity, "Assign a lot before increasing tracked stock.");
                var bucket = StockTracking.Buckets(c, line.ProductId, session.Location).FirstOrDefault(b => b.LotId == line.LotId);
                if ((bucket?.Qty ?? 0) + delta < 0) throw new PosException(PosError.InsufficientStock, "The count variance would make current stock negative. Recount this line.");
                var avg = Money.FromCost4(Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", line.ProductId)));
                var cost = delta > 0 ? line.UnitCost : avg;
                if (delta > 0)
                {
                    var newAvg = CostCalculator.WeightedAverage(Common.OnHand(c, line.ProductId), avg, line.Variance, cost);
                    Sql.Exec(c, "UPDATE products SET avg_cost_x10000=@v WHERE id=@p", ("@v", Money.ToCost4(newAvg)), ("@p", line.ProductId));
                }
                StockTracking.Move(c, line.ProductId, session.Location, line.LotId, delta, Money.ToCost4(cost), "stock_count", document, ownerId, stamp, reason);
                if (delta < 0)
                    Sql.Exec(c, "INSERT INTO stock_losses(product_id,qty_milli,cost_cents,source_type,source_id,reason,created_at) VALUES(@p,@q,@c,'stock_count',@i,@r,@t)",
                        ("@p", line.ProductId), ("@q", -delta), ("@c", Money.ToCents(-line.Variance * cost)), ("@i", document), ("@r", reason), ("@t", stamp));
            }
            FaultInjector?.Invoke("after_count_movements");
            Sql.Exec(c, "UPDATE count_sessions SET state='approved' WHERE id=@i", ("@i", sessionId));
            Common.Audit(c, clock, ownerId, null, "stock.count_approve", sessionId.ToString(), reason); return document;
        }, ownerId);
    }
}
