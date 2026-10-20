using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record StockLocationRow(long Id, string Name) { public override string ToString() => Name; }
public sealed record StockLotRow(long Id, long ProductId, string Batch, string Expiry) { public override string ToString() => $"{Batch} · {Expiry}"; }
public sealed record StockProductConfig(bool TrackExpiry, decimal ReorderPoint, decimal TargetLevel, long? SupplierId);
public sealed record StockLocationLevel(long ProductId, string Product, long LocationId, string Location, decimal Quantity, decimal UnitCost,
    bool TrackExpiry, string? EarliestExpiry)
{ public decimal ValueAtCost => Money.Round(Quantity * UnitCost); }
public sealed record ExpiryStockRow(long LotId, long ProductId, string Product, long LocationId, string Location, string Batch, string Expiry, decimal Quantity, bool Expired);
public sealed record ReorderSuggestion(long ProductId, string Product, long? SupplierId, string Supplier, decimal OnHand, decimal Sales30Days, decimal Quantity, decimal UnitCost);

public sealed partial class StockControlService(PosDatabase db, IClock clock)
{
    public Action<string>? FaultInjector { get; set; }
    public IReadOnlyList<StockLocationRow> Locations() { using var c = db.Open(); return Sql.Query(c, "SELECT id,name FROM stock_locations WHERE active=1 ORDER BY id", x => new StockLocationRow(x.GetInt64(0), x.GetString(1))); }
    public long SalesLocation { get { using var c = db.Open(); return StockTracking.SalesLocation(c); } }
    public decimal OnHand(long productId, long locationId, long? lotId = null) { using var c = db.Open(); return Money.FromMilli(StockTracking.OnHand(c, productId, locationId, lotId)); }
    public StockProductConfig ProductConfig(long productId)
    {
        using var c = db.Open();
        return Sql.Query(c, "SELECT track_expiry,reorder_point_milli,reorder_target_milli,preferred_supplier_id FROM products WHERE id=@p",
            x => new StockProductConfig(x.GetInt64(0)==1, Money.FromMilli(x.GetInt64(1)), Money.FromMilli(x.GetInt64(2)), x.IsDBNull(3)?null:x.GetInt64(3)), ("@p", productId)).Single();
    }

    public long AdjustStock(Guid operationId, long ownerId, long productId, long locationId, long? lotId, decimal quantity, decimal? unitCost, string reason)
    {
        using var c = db.Open();
        return Db.InOwnerTransaction(c, () =>
        {
            if (Operation(c, operationId, "adjustment") is long old) return old;
            StockTracking.RequireProduct(c, productId); StockTracking.RequireLocation(c, locationId);
            var qty = Money.ToMilli(quantity);
            if (qty == 0 || string.IsNullOrWhiteSpace(reason) || qty > 0 && (unitCost is null || unitCost < 0))
                throw new PosException(PosError.InvalidQuantity, "Enter a nonzero adjustment, gain cost and reason.");
            if (lotId is long lot) StockTracking.RequireLot(c, productId, lot);
            if (StockTracking.Tracked(c, productId) && lotId is null) throw new PosException(PosError.InvalidQuantity, "Select a lot for tracked stock.");
            var id = BeginOperation(c, operationId, "adjustment"); var stamp = Common.Stamp(clock);
            var avg = Money.FromCost4(Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", productId)));
            var cost = qty > 0 ? unitCost!.Value : avg;
            if (qty > 0)
            {
                var newAvg = CostCalculator.WeightedAverage(Common.OnHand(c, productId), avg, quantity, cost);
                StockTracking.Move(c, productId, locationId, lotId, qty, Money.ToCost4(cost), "stock_adjustment", id, ownerId, stamp, reason.Trim());
                Sql.Exec(c, "UPDATE products SET avg_cost_x10000=@v WHERE id=@p", ("@v", Money.ToCost4(newAvg)), ("@p", productId));
            }
            else
            {
                StockTracking.Remove(c, productId, locationId, lotId, -qty, Money.ToCost4(cost), "stock_adjustment", id, ownerId, stamp, reason.Trim());
                Sql.Exec(c, "INSERT INTO stock_losses(product_id,qty_milli,cost_cents,source_type,source_id,reason,created_at) VALUES(@p,@q,@c,'stock_adjustment',@i,@r,@t)",
                    ("@p", productId), ("@q", -qty), ("@c", Money.ToCents(-quantity * cost)), ("@i", id), ("@r", reason.Trim()), ("@t", stamp));
            }
            FaultInjector?.Invoke("after_stock_adjustment"); EndOperation(c, operationId, id);
            Common.Audit(c, clock, ownerId, null, "stock.adjustment", id.ToString(), reason.Trim(), $"product={productId};location={locationId};lot={lotId};quantity={quantity}"); return id;
        }, ownerId);
    }
    public long CreateLocation(long ownerId, string name)
    {
        using var c = db.Open();
        return Db.InOwnerTransaction(c, () =>
        {
            if (string.IsNullOrWhiteSpace(name)) throw new PosException(PosError.InvalidPrice, "Location name is required.");
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM stock_locations WHERE name=@n", ("@n", name.Trim())) > 0) throw new PosException(PosError.Duplicate, "Location already exists.");
            var id = Sql.Insert(c, "INSERT INTO stock_locations(name) VALUES(@n)", ("@n", name.Trim()));
            Common.Audit(c, clock, ownerId, null, "stock.location_create", id.ToString(), name.Trim()); return id;
        }, ownerId);
    }
    public void SetSalesLocation(long ownerId, long locationId)
    {
        using var c = db.Open();
        Db.InOwnerTransaction(c, () =>
        {
            StockTracking.RequireLocation(c, locationId);
            Sql.Exec(c, "INSERT INTO settings(key,value) VALUES('sales_location_id',@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("@v", locationId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Common.Audit(c, clock, ownerId, null, "stock.sales_location", locationId.ToString()); return 0;
        }, ownerId);
    }
    public void SetExpiryWarningDays(long ownerId, int days)
    {
        using var c = db.Open();
        Db.InOwnerTransaction(c, () =>
        {
            if (days is < 0 or > 3650) throw new PosException(PosError.InvalidQuantity, "Expiry warning must be 0–3650 days.");
            Sql.Exec(c, "INSERT INTO settings(key,value) VALUES('expiry_warning_days',@v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("@v", days.ToString()));
            Common.Audit(c, clock, ownerId, null, "stock.expiry_warning", days.ToString()); return 0;
        }, ownerId);
    }
    public void ConfigureProduct(long ownerId, long productId, bool trackExpiry, decimal reorderPoint, decimal targetLevel,
        long? supplierId, string? openingBatch = null, string? openingExpiry = null)
    {
        using var c = db.Open();
        Db.InOwnerTransaction(c, () =>
        {
            StockTracking.RequireProduct(c, productId);
            if (reorderPoint < 0 || targetLevel < reorderPoint) throw new PosException(PosError.InvalidQuantity, "Target level must be at least the reorder point.");
            if (supplierId is long supplier && Sql.Scalar<long>(c, "SELECT COUNT(*) FROM suppliers WHERE id=@i AND active=1", ("@i", supplier)) != 1)
                throw new PosException(PosError.NotFound, "Preferred supplier is unavailable.");
            var stamp = Common.Stamp(clock);
            if (trackExpiry)
            {
                var unclassified = Sql.Query(c, "SELECT location_id,SUM(qty_milli) FROM stock_movements WHERE product_id=@p AND lot_id IS NULL GROUP BY location_id HAVING SUM(qty_milli)>0",
                    x => (Location: x.GetInt64(0), Qty: x.GetInt64(1)), ("@p", productId));
                if (unclassified.Count > 0)
                {
                    StockTracking.RequireNoOpenCount(c,productId);
                    if (string.IsNullOrWhiteSpace(openingBatch) || string.IsNullOrWhiteSpace(openingExpiry))
                        throw new PosException(PosError.InvalidPrice, "Assign batch and expiry to existing stock before enabling expiry tracking.");
                    var lot = StockTracking.Lot(c, productId, openingBatch, openingExpiry);
                    var cost = Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", productId));
                    foreach (var row in unclassified)
                    {
                        StockTracking.Move(c, productId, row.Location, null, -row.Qty, cost, "lot_classification", lot, ownerId, stamp, "Assign opening lot");
                        StockTracking.Move(c, productId, row.Location, lot, row.Qty, cost, "lot_classification", lot, ownerId, stamp, "Assign opening lot");
                    }
                }
            }
            Sql.Exec(c, "UPDATE products SET track_expiry=@e,reorder_point_milli=@p,reorder_target_milli=@t,preferred_supplier_id=@s WHERE id=@i",
                ("@e", trackExpiry ? 1 : 0), ("@p", Money.ToMilli(reorderPoint)), ("@t", Money.ToMilli(targetLevel)), ("@s", supplierId), ("@i", productId));
            Common.Audit(c, clock, ownerId, null, "stock.product_config", productId.ToString(), null, $"track_expiry={trackExpiry};point={reorderPoint};target={targetLevel};supplier={supplierId}"); return 0;
        }, ownerId);
    }
    static long? Operation(SqliteConnection c, Guid operation, string kind)
    {
        var old = Sql.Query(c, "SELECT kind,target_id FROM stock_operations WHERE operation_id=@o", x => (Kind: x.GetString(0), Target: x.GetInt64(1)), ("@o", operation.ToString("N"))).FirstOrDefault();
        if (old.Kind is null) return null;
        if (old.Kind != kind) throw new PosException(PosError.Duplicate, "Operation belongs to a different stock action.");
        return old.Target;
    }
    static long BeginOperation(SqliteConnection c, Guid op, string kind) => Sql.Insert(c, "INSERT INTO stock_operations(operation_id,kind,target_id) VALUES(@o,@k,0)", ("@o", op.ToString("N")), ("@k", kind));
    static void EndOperation(SqliteConnection c, Guid op, long target) => Sql.Exec(c, "UPDATE stock_operations SET target_id=@t WHERE operation_id=@o", ("@t", target), ("@o", op.ToString("N")));

    public long ClassifyLot(Guid operationId,long ownerId,long productId,long locationId,decimal quantity,string batch,string expiry)
    {
        using var c=db.Open();
        return Db.InOwnerTransaction(c,()=>
        {
            if(Operation(c,operationId,"classification") is long old)return old;
            StockTracking.RequireProduct(c,productId);StockTracking.RequireLocation(c,locationId);
            StockTracking.RequireNoOpenCount(c,productId,locationId);
            var qty=Money.ToMilli(quantity);
            if(qty<=0 || StockTracking.ExactOnHand(c,productId,locationId,null)<qty)throw new PosException(PosError.InsufficientStock,"Not enough unclassified stock at this location.");
            var lot=StockTracking.Lot(c,productId,batch,expiry);var id=BeginOperation(c,operationId,"classification");
            var cost=Sql.Scalar<long>(c,"SELECT avg_cost_x10000 FROM products WHERE id=@p",("@p",productId));var stamp=Common.Stamp(clock);
            StockTracking.Move(c,productId,locationId,null,-qty,cost,"lot_classification",id,ownerId,stamp,"Opening lot classification");
            StockTracking.Move(c,productId,locationId,lot,qty,cost,"lot_classification",id,ownerId,stamp,"Opening lot classification");
            EndOperation(c,operationId,lot);Common.Audit(c,clock,ownerId,null,"stock.classify_lot",id.ToString(),batch,$"product={productId};location={locationId};quantity={quantity}");return lot;
        },ownerId);
    }

    public long ReceiveLot(Guid operationId, long ownerId, long productId, long locationId, decimal quantity, decimal unitCost, string batch, string expiry)
    {
        using var c = db.Open();
        return Db.InOwnerTransaction(c, () =>
        {
            if (Operation(c, operationId, "lot_receipt") is long old) return old;
            StockTracking.RequireProduct(c, productId); StockTracking.RequireLocation(c, locationId);
            if (!StockTracking.Tracked(c, productId)) throw new PosException(PosError.InvalidPrice, "Enable expiry tracking first.");
            var qty = Money.ToMilli(quantity);
            if (qty <= 0 || unitCost < 0) throw new PosException(PosError.InvalidQuantity, "Invalid received quantity or cost.");
            var lot = StockTracking.Lot(c, productId, batch, expiry);
            var document = BeginOperation(c, operationId, "lot_receipt");
            var avg = Money.FromCost4(Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", productId)));
            var newAvg = CostCalculator.WeightedAverage(Common.OnHand(c, productId), avg, quantity, unitCost);
            StockTracking.Move(c, productId, locationId, lot, qty, Money.ToCost4(unitCost), "lot_receipt", document, ownerId, Common.Stamp(clock));
            Sql.Exec(c, "UPDATE products SET avg_cost_x10000=@v WHERE id=@p", ("@v", Money.ToCost4(newAvg)), ("@p", productId));
            FaultInjector?.Invoke("after_lot_receipt"); EndOperation(c, operationId, lot);
            Common.Audit(c, clock, ownerId, null, "stock.lot_receipt", document.ToString(), batch, $"product={productId};location={locationId};qty={quantity}"); return lot;
        }, ownerId);
    }

    public long Transfer(Guid operationId, long ownerId, long productId, long fromLocation, long toLocation, decimal quantity, long? lotId = null)
    {
        using var c = db.Open();
        return Db.InOwnerTransaction(c, () =>
        {
            if (Operation(c, operationId, "transfer") is long old) return old;
            StockTracking.RequireProduct(c, productId); StockTracking.RequireLocation(c, fromLocation); StockTracking.RequireLocation(c, toLocation);
            var qty = Money.ToMilli(quantity);
            if (qty <= 0 || fromLocation == toLocation) throw new PosException(PosError.InvalidQuantity, "Select different locations and a positive quantity.");
            if (lotId is long lot) StockTracking.RequireLot(c, productId, lot);
            var buckets = StockTracking.Buckets(c, productId, fromLocation).Where(b => lotId is null || b.LotId == lotId).ToList();
            if (buckets.Sum(b => b.Qty) < qty) throw new PosException(PosError.InsufficientStock, "Not enough stock at the source location.");
            var id = BeginOperation(c, operationId, "transfer"); var rest = qty; var stamp = Common.Stamp(clock);
            var cost = Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", productId));
            foreach (var b in buckets)
            {
                var take = Math.Min(b.Qty, rest); if (take == 0) break;
                StockTracking.Move(c, productId, fromLocation, b.LotId, -take, cost, "transfer_out", id, ownerId, stamp);
                FaultInjector?.Invoke("after_transfer_out");
                StockTracking.Move(c, productId, toLocation, b.LotId, take, cost, "transfer_in", id, ownerId, stamp);
                rest -= take;
            }
            EndOperation(c, operationId, id);
            Common.Audit(c, clock, ownerId, null, "stock.transfer", id.ToString(), null, $"product={productId};from={fromLocation};to={toLocation};quantity={quantity}"); return id;
        }, ownerId);
    }

    public IReadOnlyList<StockLotRow> Lots(long productId)
    {
        using var c = db.Open();
        return Sql.Query(c, "SELECT id,product_id,batch,expiry FROM stock_lots WHERE product_id=@p ORDER BY expiry,id",
            x => new StockLotRow(x.GetInt64(0), x.GetInt64(1), x.GetString(2), x.GetString(3)), ("@p", productId));
    }
    public long DefineLot(long ownerId,long productId,string batch,string expiry)
    {
        using var c=db.Open();
        return Db.InOwnerTransaction(c,()=>
        {
            StockTracking.RequireProduct(c,productId);var id=StockTracking.Lot(c,productId,batch,expiry);
            Common.Audit(c,clock,ownerId,null,"stock.lot_define",id.ToString(),batch,expiry);return id;
        },ownerId);
    }
    public IReadOnlyList<StockLocationLevel> Levels(long? locationId = null)
    {
        using var c = db.Open();
        return Sql.Query(c, """
            SELECT p.id,p.name,m.location_id,l.name,SUM(m.qty_milli),p.avg_cost_x10000,p.track_expiry,
              (SELECT MIN(lot.expiry) FROM stock_lots lot WHERE lot.product_id=p.id
                AND (SELECT COALESCE(SUM(qty_milli),0) FROM stock_movements b WHERE b.product_id=p.id AND b.location_id=m.location_id AND b.lot_id=lot.id)>0)
            FROM stock_movements m JOIN products p ON p.id=m.product_id JOIN stock_locations l ON l.id=m.location_id
            LEFT JOIN stock_lots lot ON lot.id=m.lot_id WHERE p.active=1 AND (@l IS NULL OR m.location_id=@l)
            GROUP BY p.id,m.location_id ORDER BY p.name,l.id
            """, x => new StockLocationLevel(x.GetInt64(0), x.GetString(1), x.GetInt64(2), x.GetString(3), Money.FromMilli(x.GetInt64(4)),
                Money.FromCost4(x.GetInt64(5)), x.GetInt64(6) == 1, x.IsDBNull(7) ? null : x.GetString(7)), ("@l", locationId));
    }
    public IReadOnlyList<ExpiryStockRow> NearExpiry(int? warningDays = null)
    {
        using var c = db.Open();
        var days = warningDays ?? (int.TryParse(Sql.Scalar<string>(c, "SELECT value FROM settings WHERE key='expiry_warning_days'"), out var d) ? d : 30);
        var today = Common.Today(clock); var through = DateOnly.ParseExact(today, "yyyy-MM-dd").AddDays(days).ToString("yyyy-MM-dd");
        return Sql.Query(c, """
            SELECT lot.id,p.id,p.name,l.id,l.name,lot.batch,lot.expiry,SUM(m.qty_milli)
            FROM stock_movements m JOIN stock_lots lot ON lot.id=m.lot_id JOIN products p ON p.id=m.product_id JOIN stock_locations l ON l.id=m.location_id
            WHERE p.track_expiry=1 AND lot.expiry<=@d GROUP BY lot.id,l.id HAVING SUM(m.qty_milli)>0 ORDER BY lot.expiry,lot.id
            """, x => new ExpiryStockRow(x.GetInt64(0), x.GetInt64(1), x.GetString(2), x.GetInt64(3), x.GetString(4), x.GetString(5), x.GetString(6),
                Money.FromMilli(x.GetInt64(7)), string.CompareOrdinal(x.GetString(6), today) < 0), ("@d", through));
    }
    public IReadOnlyList<ReorderSuggestion> ReorderSuggestions()
    {
        using var c = db.Open(); var from = clock.Now.AddDays(-30).ToString("o");
        return Sql.Query(c, """
            SELECT p.id,p.name,p.preferred_supplier_id,COALESCE(s.name,''),p.reorder_point_milli,p.reorder_target_milli,p.avg_cost_x10000,
              COALESCE((SELECT SUM(qty_milli) FROM stock_movements WHERE product_id=p.id),0),
              COALESCE((SELECT SUM(sl.qty_milli) FROM sale_lines sl JOIN sales sa ON sa.id=sl.sale_id WHERE sl.product_id=p.id AND sa.created_at>=@f AND sa.status='posted'),0)
            FROM products p LEFT JOIN suppliers s ON s.id=p.preferred_supplier_id WHERE p.active=1 AND p.reorder_target_milli>0 ORDER BY s.name,p.name
            """, x => (Id: x.GetInt64(0), Name: x.GetString(1), Supplier: x.IsDBNull(2) ? (long?)null : x.GetInt64(2), SupplierName: x.GetString(3),
                Point: x.GetInt64(4), Target: x.GetInt64(5), Cost: x.GetInt64(6), Stock: x.GetInt64(7), Sales: x.GetInt64(8)), ("@f", from))
            .Where(r => r.Stock <= r.Point).Select(r => new ReorderSuggestion(r.Id, r.Name, r.Supplier, r.SupplierName, Money.FromMilli(r.Stock),
                Money.FromMilli(r.Sales), Money.FromMilli(Math.Max(0, Math.Max(r.Target, r.Sales) - r.Stock)), Money.FromCost4(r.Cost))).ToList();
    }
    public long CreateReorderOrder(Guid operationId, long ownerId, long supplierId)
    {
        using (var c = db.Open()) Common.RequireOwner(c, ownerId);
        var lines = ReorderSuggestions().Where(r => r.SupplierId == supplierId && r.Quantity > 0).Select(r => new PurchaseLine(r.ProductId, r.Quantity, r.UnitCost)).ToList();
        return new SupplierService(db, clock).CreateOrder(operationId, ownerId, supplierId, lines, null, "Reorder suggestion; owner review required");
    }
}
