using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

// All quantities remain movement-derived. A lot is identity/expiry metadata, never an editable stock total.
internal static class StockTracking
{
    internal sealed record Bucket(long? LotId, string? Batch, string? Expiry, long Qty);
    internal static bool Tracked(SqliteConnection c, long product) => Sql.Scalar<long>(c, "SELECT track_expiry FROM products WHERE id=@p", ("@p", product)) == 1;
    internal static long SalesLocation(SqliteConnection c) => long.TryParse(Sql.Scalar<string>(c, "SELECT value FROM settings WHERE key='sales_location_id'"), out var id) ? id : 1;
    internal static void RequireLocation(SqliteConnection c, long location)
    {
        if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM stock_locations WHERE id=@i AND active=1", ("@i", location)) != 1)
            throw new PosException(PosError.NotFound, "Stock location not found.");
    }
    internal static void RequireProduct(SqliteConnection c, long product)
    {
        if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM products WHERE id=@i AND active=1", ("@i", product)) != 1)
            throw new PosException(PosError.ProductUnavailable, "Product not available.");
    }
    internal static void RequireNoOpenCount(SqliteConnection c,long product,long? location=null)
    {
        if(Sql.Scalar<long>(c,"SELECT COUNT(*) FROM count_lines l JOIN count_sessions s ON s.id=l.session_id WHERE s.state='open' AND l.product_id=@p AND (@loc IS NULL OR s.location_id=@loc)",("@p",product),("@loc",location))>0)
            throw new PosException(PosError.StockCountOpen,"Finish or cancel the existing open count before opening an overlapping count or changing lot classification.");
    }
    internal static long OnHand(SqliteConnection c, long product, long location, long? lot = null) => Sql.Scalar<long>(c,
        "SELECT COALESCE(SUM(qty_milli),0) FROM stock_movements WHERE product_id=@p AND location_id=@l AND (@lot IS NULL OR lot_id=@lot)",
        ("@p", product), ("@l", location), ("@lot", lot));
    internal static long ExactOnHand(SqliteConnection c, long product, long location, long? lot) => Sql.Scalar<long>(c,
        "SELECT COALESCE(SUM(qty_milli),0) FROM stock_movements WHERE product_id=@p AND location_id=@l AND lot_id IS @lot",
        ("@p", product), ("@l", location), ("@lot", lot));
    internal static void Remove(SqliteConnection c, long product, long location, long? lot, long qty, long cost,
        string source, long sourceId, long actor, string stamp, string reason)
    {
        RequireLocation(c, location);
        if (lot is long id) RequireLot(c, product, id);
        if (Tracked(c, product) && lot is null) throw new PosException(PosError.InvalidQuantity, "Select the physical lot.");
        var buckets = Buckets(c, product, location).Where(b => lot is null || b.LotId == lot).ToList();
        if (buckets.Sum(b => b.Qty) < qty) throw new PosException(PosError.InsufficientStock, "Not enough stock at the selected location and lot.");
        var rest = qty;
        foreach (var b in buckets)
        {
            var take = Math.Min(rest, b.Qty); if (take == 0) break;
            Move(c, product, location, b.LotId, -take, cost, source, sourceId, actor, stamp, reason); rest -= take;
        }
    }
    internal static List<Bucket> Buckets(SqliteConnection c, long product, long location) => Sql.Query(c, """
        SELECT m.lot_id,l.batch,l.expiry,SUM(m.qty_milli) FROM stock_movements m LEFT JOIN stock_lots l ON l.id=m.lot_id
        WHERE m.product_id=@p AND m.location_id=@l GROUP BY m.lot_id HAVING SUM(m.qty_milli)>0
        ORDER BY CASE WHEN m.lot_id IS NULL THEN 0 ELSE 1 END,l.expiry,l.id
        """, x => new Bucket(x.IsDBNull(0) ? null : x.GetInt64(0), x.IsDBNull(1) ? null : x.GetString(1), x.IsDBNull(2) ? null : x.GetString(2), x.GetInt64(3)),
        ("@p", product), ("@l", location));
    internal static long Lot(SqliteConnection c, long product, string batch, string expiry)
    {
        if (string.IsNullOrWhiteSpace(batch)) throw new PosException(PosError.InvalidPrice, "Batch number and expiry are required.");
        InstallmentPlans.CheckDate(expiry);
        var old = Sql.Scalar<long?>(c, "SELECT id FROM stock_lots WHERE product_id=@p AND batch=@b AND expiry=@e", ("@p", product), ("@b", batch.Trim()), ("@e", expiry));
        return old ?? Sql.Insert(c, "INSERT INTO stock_lots(product_id,batch,expiry) VALUES(@p,@b,@e)", ("@p", product), ("@b", batch.Trim()), ("@e", expiry));
    }
    internal static void RequireLot(SqliteConnection c, long product, long lot)
    {
        if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM stock_lots WHERE id=@i AND product_id=@p", ("@i", lot), ("@p", product)) != 1)
            throw new PosException(PosError.NotFound, "Select a lot belonging to this product.");
    }
    internal static void Move(SqliteConnection c, long product, long location, long? lot, long qty, long cost,
        string source, long sourceId, long actor, string stamp, string? reason = null) => Sql.Exec(c, """
        INSERT INTO stock_movements(product_id,location_id,lot_id,qty_milli,unit_cost_x10000,source_type,source_id,reason,actor_id,created_at)
        VALUES(@p,@l,@lot,@q,@u,@s,@i,@r,@a,@t)
        """, ("@p", product), ("@l", location), ("@lot", lot), ("@q", qty), ("@u", cost), ("@s", source), ("@i", sourceId), ("@r", reason), ("@a", actor), ("@t", stamp));

    internal static long? Receive(SqliteConnection c, long product, long location, string? batch, string? expiry, long qty,
        long cost, string source, long sourceId, long actor, string stamp)
    {
        RequireLocation(c, location);
        long? lot = null;
        if (Tracked(c, product))
        {
            if (string.IsNullOrWhiteSpace(batch) || string.IsNullOrWhiteSpace(expiry)) throw new PosException(PosError.InvalidPrice, "Enter batch and expiry for this product.");
            lot = Lot(c, product, batch, expiry);
        }
        Move(c, product, location, lot, qty, cost, source, sourceId, actor, stamp);
        return lot;
    }

    internal static void ConsumeSale(SqliteConnection c, long product, long sale, long saleLine, long qty, long cost,
        long actor, string stamp, string today, bool expiredApproved)
    {
        var location = SalesLocation(c); RequireLocation(c, location);
        var tracked = Tracked(c, product);
        var buckets = Buckets(c, product, location).Where(b => !tracked || b.LotId is not null && (expiredApproved || string.CompareOrdinal(b.Expiry, today) >= 0)).ToList();
        if (buckets.Sum(b => b.Qty) < qty)
            throw new PosException(tracked ? PosError.ExpiredStock : PosError.InsufficientStock, tracked ? "Not enough unexpired stock at the sales location. Owner approval with a reason is required for expired lots." : "Not enough stock at the sales location.");
        var rest = qty;
        foreach (var b in buckets)
        {
            var take = Math.Min(b.Qty, rest); if (take == 0) break;
            Sql.Exec(c, "INSERT INTO sale_line_stock(sale_line_id,location_id,lot_id,qty_milli) VALUES(@s,@l,@lot,@q)", ("@s", saleLine), ("@l", location), ("@lot", b.LotId), ("@q", take));
            Move(c, product, location, b.LotId, -take, cost, "sale", sale, actor, stamp);
            rest -= take;
        }
    }

    internal static void ReturnSale(SqliteConnection c, long product, long saleLine, long returnLine, long returnId, long qty,
        long cost, bool resalable, long? selectedLot, long actor, string stamp)
    {
        var allocations = Sql.Query(c, """
            SELECT s.id,s.location_id,s.lot_id,s.qty_milli-COALESCE((SELECT SUM(qty_milli) FROM return_stock_allocations WHERE sale_allocation_id=s.id),0)
            FROM sale_line_stock s WHERE s.sale_line_id=@s ORDER BY s.id
            """, x => (Id: x.GetInt64(0), Location: x.GetInt64(1), Lot: x.IsDBNull(2) ? (long?)null : x.GetInt64(2), Left: x.GetInt64(3)), ("@s", saleLine));
        if (allocations.Count == 0) // Sale predates location/lot tracking; its original location was Main.
        {
            if (resalable && Tracked(c, product) && selectedLot is null) throw new PosException(PosError.InvalidQuantity, "Select the physical return lot for this historical sale.");
            if (selectedLot is long lot) RequireLot(c, product, lot);
            if (resalable) Move(c, product, 1, selectedLot, qty, cost, "return", returnId, actor, stamp, "resalable return");
            return;
        }
        var rest = qty;
        foreach (var a in allocations.Where(a => a.Left > 0 && (selectedLot is null || a.Lot is null || a.Lot == selectedLot)))
        {
            var take = Math.Min(a.Left, rest); if (take == 0) break;
            var lot = a.Lot ?? selectedLot;
            if (resalable && Tracked(c, product) && lot is null) throw new PosException(PosError.InvalidQuantity, "Select the returned lot.");
            if (lot is long id) RequireLot(c, product, id);
            Sql.Exec(c, "INSERT INTO return_stock_allocations(return_line_id,sale_allocation_id,qty_milli) VALUES(@r,@a,@q)", ("@r", returnLine), ("@a", a.Id), ("@q", take));
            if (resalable) Move(c, product, a.Location, lot, take, cost, "return", returnId, actor, stamp, "resalable return");
            rest -= take;
        }
        if (rest != 0) throw new PosException(PosError.InvalidQuantity, "Returned quantity exceeds the selected original lot allocation.");
    }
}
