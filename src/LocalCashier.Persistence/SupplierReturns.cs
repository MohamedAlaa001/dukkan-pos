using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record SupplierPurchaseLineRow(long Id, long ProductId, string Description, decimal Quantity, decimal UnitCost, decimal Returned, decimal Total, decimal ReturnedCredit, long LocationId = 1, long? LotId = null);
public sealed record SupplierReturnLine(long ProductId, decimal Quantity, decimal UnitCost, long? InvoiceLineId = null, long? LocationId = null, long? LotId = null);
public sealed record SupplierReturnRequest(Guid OperationId, long ActorId, long SupplierId, string Date, string Reason, string? Notes,
    long? InvoiceId, IReadOnlyList<SupplierReturnLine> Lines);
public sealed record SupplierReturnResult(long Id, string Number, decimal Total, bool AlreadyPosted);
public sealed record SupplierReturnRow(long Id, long SupplierId, string Number, string Date, string Reason, string? Notes, long? InvoiceId, decimal Total, bool Reversed);

public sealed partial class SupplierService
{
    public IReadOnlyList<(long ProductId,string Product)> ReturnRestockNeeds(long returnId)
    {
        using var c=db.Open();
        return Sql.Query(c,"SELECT DISTINCT p.id,p.name FROM stock_movements m JOIN products p ON p.id=m.product_id WHERE m.source_type='supplier_return' AND m.source_id=@i AND m.lot_id IS NULL AND p.track_expiry=1",
            x=>(x.GetInt64(0),x.GetString(1)),("@i",returnId));
    }
    public IReadOnlyList<SupplierPurchaseLineRow> PurchaseLines(long invoiceId)
    {
        using var c = db.Open();
        return Sql.Query(c, """
            SELECT l.id,l.product_id,l.description,l.qty_milli,l.unit_cost_x10000,l.line_cents,
              COALESCE((SELECT SUM(r.qty_milli) FROM supplier_return_lines r WHERE r.invoice_line_id=l.id
              AND r.return_id NOT IN(SELECT return_id FROM supplier_return_reversals)),0),
              COALESCE((SELECT SUM(r.line_cents) FROM supplier_return_lines r WHERE r.invoice_line_id=l.id
              AND r.return_id NOT IN(SELECT return_id FROM supplier_return_reversals)),0),l.location_id,l.lot_id
            FROM supplier_invoice_lines l WHERE l.invoice_id=@i ORDER BY l.id
            """, x => new SupplierPurchaseLineRow(x.GetInt64(0), x.GetInt64(1), x.GetString(2), Money.FromMilli(x.GetInt64(3)),
                Money.FromCost4(x.GetInt64(4)), Money.FromMilli(x.GetInt64(6)), Money.FromCents(x.GetInt64(5)), Money.FromCents(x.GetInt64(7)), x.GetInt64(8), x.IsDBNull(9) ? null : x.GetInt64(9)), ("@i", invoiceId));
    }

    public decimal AvailableCredit(long supplierId) { using var c = db.Open(); return Money.FromCents(Credits(c, supplierId).Sum(r => r.Left)); }

    static List<(long Id, long Left)> Credits(SqliteConnection c, long supplierId) => Sql.Query(c, """
        SELECT r.id,r.total_cents-COALESCE((SELECT SUM(amount_cents) FROM supplier_credit_allocations
          WHERE return_id=r.id AND id NOT IN(SELECT allocation_id FROM supplier_credit_allocation_reversals)),0)
        FROM supplier_returns r WHERE r.supplier_id=@s AND r.id NOT IN(SELECT return_id FROM supplier_return_reversals) ORDER BY r.id
        """, x => (x.GetInt64(0), x.GetInt64(1)), ("@s", supplierId)).Where(r => r.Item2 > 0).ToList();

    static void ApplyAvailableCredits(SqliteConnection c, long supplierId, string stamp, long? preferredInvoice = null)
    {
        var open = OpenInvoices(c, supplierId).OrderByDescending(i => i.Id == preferredInvoice).ToList();
        foreach (var credit in Credits(c, supplierId))
        {
            var remaining = credit.Left;
            for (var n = 0; n < open.Count && remaining > 0; n++)
            {
                var invoice = open[n];
                var take = Math.Min(invoice.Left, remaining);
                if (take <= 0) continue;
                Sql.Exec(c, "INSERT INTO supplier_credit_allocations(return_id,invoice_id,amount_cents,created_at) VALUES(@r,@i,@a,@t)",
                    ("@r", credit.Id), ("@i", invoice.Id), ("@a", take), ("@t", stamp));
                open[n] = invoice with { Left = invoice.Left - take };
                remaining -= take;
            }
        }
    }

    public SupplierReturnResult PostReturn(SupplierReturnRequest r)
    {
        using var c = db.Open();
        return Db.InOwnerTransaction(c, () =>
        {
            var dup = Sql.Query(c, "SELECT id,number,total_cents FROM supplier_returns WHERE operation_id=@o",
                x => new SupplierReturnResult(x.GetInt64(0), x.GetString(1), Money.FromCents(x.GetInt64(2)), true), ("@o", r.OperationId.ToString("N"))).FirstOrDefault();
            if (dup is not null) return dup;
            RequireSupplier(c, r.SupplierId);
            InstallmentPlans.CheckDate(r.Date);
            if (r.Reason is not ("damaged" or "wrong_item" or "expired" or "other")) throw new PosException(PosError.InvalidDiscount, "Select a return reason.");
            if (r.InvoiceId is null && string.IsNullOrWhiteSpace(r.Notes)) throw new PosException(PosError.InvalidDiscount, "Unlinked returns require an approval reason.");
            if (r.InvoiceId is long invoice && Sql.Scalar<long>(c, """
                SELECT COUNT(*) FROM supplier_invoices WHERE id=@i AND supplier_id=@s AND kind='purchase'
                  AND id NOT IN(SELECT target_id FROM supplier_reversals WHERE target_type='invoice')
                """, ("@i", invoice), ("@s", r.SupplierId)) != 1) throw new PosException(PosError.NotFound, "Purchase invoice not available.");
            if (r.Lines.Count == 0) throw new PosException(PosError.EmptyCart, "A return needs lines.");
            if (r.Lines.Where(l => l.InvoiceLineId is not null).GroupBy(l => l.InvoiceLineId).Any(g => g.Count() > 1))
                throw new PosException(PosError.InvalidQuantity, "Combine quantities for each original line.");
            var prepared = new List<(SupplierReturnLine Line, long Qty, long Cost, long StockCost, long Cents, string Description, long Location, long? Lot)>();
            foreach (var line in r.Lines)
            {
                var qty = Money.ToMilli(line.Quantity);
                if (qty <= 0 || line.UnitCost < 0) throw new PosException(PosError.InvalidQuantity, "Invalid return quantity or cost.");
                var info = ProductInfo(c, line.ProductId);
                if (info.Name is null) throw new PosException(PosError.ProductUnavailable, "Product not found.");
                var cost = Money.ToCost4(line.UnitCost);
                var location = line.LocationId ?? 1; var lot = line.LotId;
                long cents;
                if (r.InvoiceId is long original)
                {
                    var source = Sql.Query(c, "SELECT qty_milli,unit_cost_x10000,line_cents,description,location_id,lot_id FROM supplier_invoice_lines WHERE id=@l AND invoice_id=@i AND product_id=@p",
                        x => (Qty: x.GetInt64(0), Cost: x.GetInt64(1), Cents: x.GetInt64(2), Description: x.GetString(3), Location: x.GetInt64(4), Lot: x.IsDBNull(5) ? (long?)null : x.GetInt64(5)),
                        ("@l", line.InvoiceLineId), ("@i", original), ("@p", line.ProductId)).FirstOrDefault();
                    if (source.Qty == 0) throw new PosException(PosError.NotFound, "Original purchase line not found.");
                    location = line.LocationId ?? source.Location;
                    if (source.Lot is long originalLot && lot is long selectedLot && originalLot != selectedLot)
                        throw new PosException(PosError.InvalidQuantity, "Select the original purchased lot.");
                    lot ??= source.Lot;
                    var used = Sql.Query(c, """
                        SELECT COALESCE(SUM(qty_milli),0),COALESCE(SUM(line_cents),0) FROM supplier_return_lines
                        WHERE invoice_line_id=@l AND return_id NOT IN(SELECT return_id FROM supplier_return_reversals)
                        """, x => (Qty: x.GetInt64(0), Cents: x.GetInt64(1)), ("@l", line.InvoiceLineId)).Single();
                    if (qty > source.Qty - used.Qty) throw new PosException(PosError.InvalidQuantity, "Cannot return more than purchased.");
                    // A reversal may leave active credits from different rounding positions. Never claw back
                    // a posted piastre in a later return; distribute the uncredited remainder, exactly on full return.
                    cents = Math.Clamp((long)decimal.Round((decimal)source.Cents * (used.Qty + qty) / source.Qty, 0, MidpointRounding.AwayFromZero) - used.Cents,
                        0, source.Cents - used.Cents);
                    cost = source.Cost;
                }
                else
                {
                    if (line.InvoiceLineId is not null) throw new PosException(PosError.NotFound, "Invoice reference is missing.");
                    cents = Money.ToCents(line.Quantity * line.UnitCost);
                }
                var stockCost = Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", line.ProductId));
                prepared.Add((line, qty, cost, stockCost, cents, string.Join(" ", new[] { info.Name, info.Size, info.Color }.Where(s => !string.IsNullOrWhiteSpace(s))), location, lot));
            }
            foreach (var group in prepared.GroupBy(l => l.Line.ProductId))
                if (Money.ToMilli(Common.OnHand(c, group.Key)) < group.Sum(l => l.Qty)) throw new PosException(PosError.InsufficientStock, "Not enough stock to return.");
            var stamp = Common.Stamp(clock);
            var number = Common.NextNumber(c, "SR");
            var total = prepared.Sum(l => l.Cents);
            var id = Sql.Insert(c, """
                INSERT INTO supplier_returns(operation_id,number,supplier_id,invoice_id,return_date,reason,notes,total_cents,actor_id,created_at)
                VALUES(@o,@n,@s,@i,@d,@r,@nt,@v,@a,@t)
                """, ("@o", r.OperationId.ToString("N")), ("@n", number), ("@s", r.SupplierId), ("@i", r.InvoiceId), ("@d", r.Date),
                ("@r", r.Reason), ("@nt", r.Notes), ("@v", total), ("@a", r.ActorId), ("@t", stamp));
            foreach (var l in prepared)
            {
                Sql.Exec(c, """
                    INSERT INTO supplier_return_lines(return_id,product_id,invoice_line_id,description,qty_milli,unit_cost_x10000,stock_cost_x10000,line_cents,location_id,lot_id)
                    VALUES(@r,@p,@i,@d,@q,@u,@sc,@v,@loc,@lot)
                    """, ("@r", id), ("@p", l.Line.ProductId), ("@i", l.Line.InvoiceLineId), ("@d", l.Description), ("@q", l.Qty), ("@u", l.Cost), ("@sc", l.StockCost), ("@v", l.Cents), ("@loc", l.Location), ("@lot", l.Lot));
                StockTracking.Remove(c, l.Line.ProductId, l.Location, l.Lot, l.Qty, l.StockCost, "supplier_return", id, r.ActorId, stamp, r.Reason);
            }
            FaultInjector?.Invoke("after_supplier_return_stock");
            Sql.Exec(c, "INSERT INTO supplier_ledger(supplier_id,entry_type,amount_cents,source_type,source_id,entry_date,created_at) VALUES(@s,'return',@a,'supplier_return',@i,@d,@t)",
                ("@s", r.SupplierId), ("@a", -total), ("@i", id), ("@d", r.Date), ("@t", stamp));
            ApplyAvailableCredits(c, r.SupplierId, stamp, r.InvoiceId);
            Common.Audit(c, clock, r.ActorId, r.ActorId, "supplier.return", number, r.Reason, r.Notes);
            return new SupplierReturnResult(id, number, Money.FromCents(total), false);
        }, r.ActorId);
    }

    public void ReverseReturn(Guid operationId, long ownerId, long returnId, string reason, IReadOnlyDictionary<long,long>? restockLots=null)
    {
        using var c = db.Open();
        Db.InOwnerTransaction(c, () =>
        {
            var dup = Sql.Scalar<long?>(c, "SELECT return_id FROM supplier_return_reversals WHERE operation_id=@o", ("@o", operationId.ToString("N")));
            if (dup is long old) { if (old != returnId) throw new PosException(PosError.Duplicate, "Operation already used."); return 0; }
            if (string.IsNullOrWhiteSpace(reason)) throw new PosException(PosError.InvalidDiscount, "A reason is required.");
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM supplier_return_reversals WHERE return_id=@i", ("@i", returnId)) > 0)
                throw new PosException(PosError.Duplicate, "Already reversed.");
            var source = Sql.Query(c, "SELECT supplier_id,total_cents,number FROM supplier_returns WHERE id=@i",
                x => (Supplier: x.GetInt64(0), Total: x.GetInt64(1), Number: x.GetString(2)), ("@i", returnId)).FirstOrDefault();
            if (source.Number is null) throw new PosException(PosError.NotFound, "Return not found.");
            var stamp = Common.Stamp(clock);
            Sql.Exec(c, "INSERT INTO supplier_return_reversals(operation_id,return_id,reason,actor_id,created_at) VALUES(@o,@i,@r,@a,@t)",
                ("@o", operationId.ToString("N")), ("@i", returnId), ("@r", reason), ("@a", ownerId), ("@t", stamp));
            foreach (var line in Sql.Query(c, "SELECT product_id,-qty_milli,unit_cost_x10000,location_id,lot_id FROM stock_movements WHERE source_type='supplier_return' AND source_id=@i ORDER BY id",
                x => (Product: x.GetInt64(0), Qty: x.GetInt64(1), Cost: x.GetInt64(2), Location: x.GetInt64(3), Lot: x.IsDBNull(4) ? (long?)null : x.GetInt64(4)), ("@i", returnId)))
            {
                var lot=line.Lot;
                if(lot is null && StockTracking.Tracked(c,line.Product))
                {
                    if(restockLots is null || !restockLots.TryGetValue(line.Product,out var selected))throw new PosException(PosError.StockLotRequired,"Select a physical lot for the historical return reversal.");
                    StockTracking.RequireLot(c,line.Product,selected);lot=selected;
                }
                var avg = Money.FromCost4(Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", line.Product)));
                var newAvg = CostCalculator.WeightedAverage(Common.OnHand(c, line.Product), avg, Money.FromMilli(line.Qty), Money.FromCost4(line.Cost));
                Sql.Exec(c, "UPDATE products SET avg_cost_x10000=@u WHERE id=@p", ("@u", Money.ToCost4(newAvg)), ("@p", line.Product));
                StockTracking.Move(c, line.Product, line.Location, lot, line.Qty, line.Cost, "supplier_return_reversal", returnId, ownerId, stamp, reason);
            }
            Sql.Exec(c, "INSERT INTO supplier_ledger(supplier_id,entry_type,amount_cents,source_type,source_id,entry_date,created_at) VALUES(@s,'return_reversal',@a,'supplier_return',@i,@d,@t)",
                ("@s", source.Supplier), ("@a", source.Total), ("@i", returnId), ("@d", Common.Today(clock)), ("@t", stamp));
            FaultInjector?.Invoke("after_supplier_return_reversal");
            ApplyAvailableCredits(c, source.Supplier, stamp);
            Common.Audit(c, clock, ownerId, null, "supplier.return_reverse", source.Number, reason);
            return 0;
        }, ownerId);
    }

    public IReadOnlyList<SupplierReturnRow> ReturnList(long? supplierId = null)
    {
        using var c = db.Open();
        return Sql.Query(c, """
            SELECT id,supplier_id,number,return_date,reason,notes,invoice_id,total_cents,
              EXISTS(SELECT 1 FROM supplier_return_reversals WHERE return_id=r.id)
            FROM supplier_returns r WHERE (@s IS NULL OR supplier_id=@s) ORDER BY id DESC
            """, x => new SupplierReturnRow(x.GetInt64(0), x.GetInt64(1), x.GetString(2), x.GetString(3), x.GetString(4),
                x.IsDBNull(5) ? null : x.GetString(5), x.IsDBNull(6) ? null : x.GetInt64(6), Money.FromCents(x.GetInt64(7)), x.GetInt64(8) == 1), ("@s", supplierId));
    }
}
