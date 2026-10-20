using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record PurchaseOrderRow(long Id, long SupplierId, string Supplier, string Number, string Status, string? ExpectedDate,
    string? Notes, decimal Total, decimal Ordered, decimal Received, bool Overdue);
public sealed record PurchaseOrderLineRow(long Id, long ProductId, string Description, decimal Quantity, decimal UnitCost, decimal Received)
{
    public decimal Remaining => Math.Max(0, Quantity - Received);
}

public sealed partial class SupplierService
{
    public long CreateOrder(Guid operationId, long ownerId, long supplierId, IReadOnlyList<PurchaseLine> lines, string? expectedDate, string? notes)
    {
        using var c = db.Open();
        return Db.InOwnerTransaction(c, () =>
        {
            var duplicate = Sql.Scalar<long?>(c, "SELECT id FROM purchase_orders WHERE operation_id=@o", ("@o", operationId.ToString("N")));
            if (duplicate is long id) return id;
            RequireSupplier(c, supplierId);
            if (expectedDate is not null) InstallmentPlans.CheckDate(expectedDate);
            if (lines.Count == 0) throw new PosException(PosError.EmptyCart, "An order needs lines.");
            var stamp = Common.Stamp(clock);
            var number = Common.NextNumber(c, "PO");
            var order = Sql.Insert(c, """
                INSERT INTO purchase_orders(operation_id,number,supplier_id,state,expected_date,notes,actor_id,created_at)
                VALUES(@o,@n,@s,'draft',@d,@nt,@a,@t)
                """, ("@o", operationId.ToString("N")), ("@n", number), ("@s", supplierId), ("@d", expectedDate), ("@nt", notes), ("@a", ownerId), ("@t", stamp));
            foreach (var line in lines)
            {
                var info = ProductInfo(c, line.ProductId);
                if (info.Name is null) throw new PosException(PosError.ProductUnavailable, "Product unavailable.");
                if (line.Quantity <= 0) throw new PosException(PosError.InvalidQuantity, "Quantity must be positive.");
                if (line.UnitCost < 0) throw new PosException(PosError.InvalidPrice, "Cost cannot be negative.");
                Sql.Exec(c, """
                    INSERT INTO purchase_order_lines(order_id,product_id,description,qty_milli,unit_cost_x10000) VALUES(@o,@p,@d,@q,@u)
                    """, ("@o", order), ("@p", line.ProductId), ("@d", line.Description ?? string.Join(" ", new[] { info.Name, info.Size, info.Color }.Where(s => !string.IsNullOrWhiteSpace(s)))),
                    ("@q", Money.ToMilli(line.Quantity)), ("@u", Money.ToCost4(line.UnitCost)));
            }
            FaultInjector?.Invoke("after_order_lines");
            Common.Audit(c, clock, ownerId, null, "supplier.order_create", number);
            return order;
        }, ownerId);
    }

    public void TransitionOrder(Guid operationId, long ownerId, long orderId, string state, string? reason = null)
    {
        using var c = db.Open();
        Db.InOwnerTransaction(c, () =>
        {
            var dup = Sql.Query(c, "SELECT order_id,state FROM purchase_order_actions WHERE operation_id=@o", x => (Id: x.GetInt64(0), State: x.GetString(1)), ("@o", operationId.ToString("N"))).FirstOrDefault();
            if (dup.State is not null)
            {
                if (dup.Id != orderId || dup.State != state) throw new PosException(PosError.Duplicate, "Operation already used.");
                return 0;
            }
            var current = Sql.Scalar<string>(c, "SELECT state FROM purchase_orders WHERE id=@i", ("@i", orderId));
            if (current is null) throw new PosException(PosError.NotFound, "Order not found.");
            if (!(current == "draft" && state == "open" || current is "draft" or "open" && state == "cancelled"))
                throw new PosException(PosError.PermissionDenied, "Invalid order transition.");
            if (state == "cancelled" && string.IsNullOrWhiteSpace(reason)) throw new PosException(PosError.InvalidDiscount, "A reason is required.");
            if (state == "cancelled" && OrderLines(c, orderId).All(l => l.Remaining == 0))
                throw new PosException(PosError.PermissionDenied, "A completed order cannot be cancelled.");
            Sql.Exec(c, "UPDATE purchase_orders SET state=@s WHERE id=@i", ("@s", state), ("@i", orderId));
            Sql.Exec(c, "INSERT INTO purchase_order_actions(operation_id,order_id,state,reason,actor_id,created_at) VALUES(@o,@i,@s,@r,@a,@t)",
                ("@o", operationId.ToString("N")), ("@i", orderId), ("@s", state), ("@r", reason), ("@a", ownerId), ("@t", Common.Stamp(clock)));
            Common.Audit(c, clock, ownerId, null, "supplier.order_" + state, orderId.ToString(), reason);
            return 0;
        }, ownerId);
    }

    static List<PurchaseOrderLineRow> OrderLines(SqliteConnection c, long orderId) => Sql.Query(c, """
        SELECT l.id,l.product_id,l.description,l.qty_milli,l.unit_cost_x10000,
          COALESCE((SELECT SUM(r.qty_milli) FROM purchase_order_receipts r JOIN supplier_invoice_lines il ON il.id=r.invoice_line_id
            WHERE r.order_line_id=l.id AND il.invoice_id NOT IN(SELECT target_id FROM supplier_reversals WHERE target_type='invoice')),0)
        FROM purchase_order_lines l WHERE l.order_id=@o ORDER BY l.id
        """, x => new PurchaseOrderLineRow(x.GetInt64(0), x.GetInt64(1), x.GetString(2), Money.FromMilli(x.GetInt64(3)), Money.FromCost4(x.GetInt64(4)), Money.FromMilli(x.GetInt64(5))), ("@o", orderId));

    public IReadOnlyList<PurchaseOrderLineRow> OrderLines(long orderId) { using var c = db.Open(); return OrderLines(c, orderId); }

    public IReadOnlyList<PurchaseOrderRow> OrderList(long? supplierId = null, bool openOnly = false)
    {
        using var c = db.Open();
        var today = Common.Today(clock);
        return Sql.Query(c, """
            SELECT o.id,o.supplier_id,s.name,o.number,o.state,o.expected_date,o.notes FROM purchase_orders o JOIN suppliers s ON s.id=o.supplier_id
            WHERE (@s IS NULL OR o.supplier_id=@s) ORDER BY o.id DESC
            """, x => (Id: x.GetInt64(0), Supplier: x.GetInt64(1), Name: x.GetString(2), Number: x.GetString(3), State: x.GetString(4),
                Date: x.IsDBNull(5) ? null : x.GetString(5), Notes: x.IsDBNull(6) ? null : x.GetString(6)), ("@s", supplierId))
            .Select(o =>
            {
                var lines = OrderLines(c, o.Id);
                var status = o.State == "open" ? lines.All(l => l.Remaining == 0) ? "received" : lines.Any(l => l.Received > 0) ? "partial" : "open" : o.State;
                return new PurchaseOrderRow(o.Id, o.Supplier, o.Name, o.Number, status, o.Date, o.Notes,
                    lines.Sum(l => Money.FromCents(Money.ToCents(l.Quantity * l.UnitCost))), lines.Sum(l => l.Quantity), lines.Sum(l => l.Received),
                    status is "open" or "partial" && o.Date is not null && string.CompareOrdinal(o.Date, today) < 0);
            }).Where(o => !openOnly || o.Status is "open" or "partial").ToList();
    }

    void ValidateOrderReceipt(SqliteConnection c, PostPurchaseRequest r)
    {
        if (r.OrderId is not long order)
        {
            if (r.Lines.Any(l => l.OrderLineId is not null)) throw new PosException(PosError.PaymentMismatch, "An order reference is required.");
            return;
        }
        if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM purchase_orders WHERE id=@i AND supplier_id=@s AND state='open'", ("@i", order), ("@s", r.SupplierId)) != 1)
            throw new PosException(PosError.PermissionDenied, "Order is not open for this supplier.");
        var available = OrderLines(c, order);
        foreach (var group in r.Lines.GroupBy(l => l.OrderLineId))
        {
            var line = available.FirstOrDefault(l => l.Id == group.Key);
            if (line is null || group.Any(l => l.ProductId != line.ProductId)) throw new PosException(PosError.ProductUnavailable, "Order line does not match.");
            if (group.Sum(l => l.Quantity) > line.Remaining)
            {
                if (!r.ConfirmOverReceipt) throw new PosException(PosError.PermissionDenied, "Confirm receipt above the ordered quantity.");
                Common.Audit(c, clock, r.ActorId, r.ActorId, "supplier.order_overreceipt", order.ToString(), r.Notes, $"line={line.Id};quantity={group.Sum(l => l.Quantity)}");
            }
        }
    }
}
