using LocalCashier.Application;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

// ---------------------------------------------------------------------------------------------------------------------
// Supplier accounts. The amount owed is always derived from supplier_ledger (purchases +, payments -, reversals opposite).
// Posted rows are never edited or deleted; corrections are reversal rows linked to their target.
// Orders and supplier returns are implemented in focused partial-class modules.
// ---------------------------------------------------------------------------------------------------------------------

public sealed record SupplierRow(long Id, string Name, string? Phone, string? Address, string? Notes, bool Active,
    decimal Balance, decimal TotalPurchases, decimal TotalPaid, decimal Overdue, decimal Opening = 0m);

public sealed record PurchaseLine(long ProductId, decimal Quantity, decimal UnitCost, string? Description = null, long? OrderLineId = null,
    long LocationId = 1, string? Batch = null, string? Expiry = null);

public sealed record PostPurchaseRequest(
    Guid OperationId, long ActorId, long SupplierId, string? SupplierRef, string InvoiceDate, string? DueDate,
    IReadOnlyList<PurchaseLine> Lines, decimal InitialPayment = 0m, PaymentMethod Method = PaymentMethod.Cash,
    long? ShiftId = null, string? Notes = null, string? PaymentReference = null, long? OrderId = null, bool ConfirmOverReceipt = false, long? MethodId=null,CardConfirmation? CardConfirmation=null);

public sealed record PurchaseResult(long InvoiceId, string Number, decimal Total, decimal Paid, decimal Outstanding, bool AlreadyPosted);

public sealed record SupplierAllocation(long InvoiceId, decimal Amount);

public sealed record SupplierPayRequest(
    Guid OperationId, long ActorId, long SupplierId, string PayDate, decimal Amount, PaymentMethod Method,
    string? Reference = null, string? Notes = null, long? ShiftId = null, IReadOnlyList<SupplierAllocation>? Allocations = null,long? MethodId=null,CardConfirmation? CardConfirmation=null);

public sealed record SupplierPayResult(long PaymentId, string Number, IReadOnlyList<SupplierAllocation> Allocations, bool AlreadyPosted);

public sealed record SupplierInvoiceRow(long Id, long SupplierId, string Supplier, string Number, string Kind, string? SupplierRef,
    string InvoiceDate, string? DueDate, decimal Total, decimal Paid, decimal Outstanding, string Status, bool Overdue, bool Reversed);

public sealed record SupplierInvoiceLineRow(string Description, decimal Quantity, decimal UnitCost, decimal Total);

public sealed record SupplierPaymentRow(long Id, string Number, string Date, string Method, decimal Amount, string? Reference, bool Reversed, string Allocated,
    long? MethodId=null,string? MethodNameEn=null,string? MethodNameAr=null);

public sealed record SupplierStatementRow(string Date, string Type, string Document, decimal Amount, decimal Running);

public sealed record SupplierDueSummary(decimal Outstanding, decimal DueToday, decimal Overdue);

public sealed partial class SupplierService(PosDatabase db, IClock clock)
{
    public Action<string>? FaultInjector { get; set; }

    // ---------------- records ----------------

    public long Create(long ownerId, string name, string? phone = null, string? address = null, string? notes = null)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (string.IsNullOrWhiteSpace(name)) throw new PosException(PosError.InvalidPrice, "A supplier name is required.");
            var id = Sql.Insert(c, "INSERT INTO suppliers(name,phone,address,notes,created_at) VALUES(@n,@p,@a,@o,@t)",
                ("@n", name.Trim()), ("@p", phone), ("@a", address), ("@o", notes), ("@t", Common.Stamp(clock)));
            Common.Audit(c, clock, ownerId, null, "supplier.create", name.Trim());
            return id;
        });
    }

    public void Update(long ownerId, long supplierId, string name, string? phone, string? address, string? notes)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (string.IsNullOrWhiteSpace(name)) throw new PosException(PosError.InvalidPrice, "A supplier name is required.");
            Sql.Exec(c, "UPDATE suppliers SET name=@n,phone=@p,address=@a,notes=@o WHERE id=@i",
                ("@n", name.Trim()), ("@p", phone), ("@a", address), ("@o", notes), ("@i", supplierId));
            Common.Audit(c, clock, ownerId, null, "supplier.update", supplierId.ToString());
            return 0;
        });
    }

    /// <summary>Suppliers are archived, never deleted. A supplier we still owe cannot be archived.</summary>
    public void SetActive(long ownerId, long supplierId, bool active)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (!active && Balance(c, supplierId) != 0)
                throw new PosException(PosError.PermissionDenied, "Settle the amount owed before archiving this supplier.");
            Sql.Exec(c, "UPDATE suppliers SET active=@a WHERE id=@i", ("@a", active ? 1 : 0), ("@i", supplierId));
            Common.Audit(c, clock, ownerId, null, active ? "supplier.restore" : "supplier.archive", supplierId.ToString());
            return 0;
        });
    }

    static long Balance(SqliteConnection c, long supplierId) =>
        Sql.Scalar<long>(c, "SELECT COALESCE(SUM(amount_cents),0) FROM supplier_ledger WHERE supplier_id=@s", ("@s", supplierId));

    public IReadOnlyList<SupplierRow> List(string? search = null, bool includeArchived = false)
    {
        using var c = db.Open();
        var today = Common.Today(clock);
        var like = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%";
        return Sql.Query(c,
            """
            SELECT s.id,s.name,s.phone,s.address,s.notes,s.active,
              COALESCE((SELECT SUM(amount_cents) FROM supplier_ledger WHERE supplier_id=s.id),0),
              COALESCE((SELECT SUM(l.amount_cents) FROM supplier_ledger l WHERE l.supplier_id=s.id AND l.entry_type IN('purchase','purchase_reversal')
                AND NOT EXISTS(SELECT 1 FROM supplier_invoices i WHERE l.source_type='invoice' AND i.id=l.source_id AND i.kind='opening')),0),
              COALESCE((SELECT -SUM(amount_cents) FROM supplier_ledger WHERE supplier_id=s.id AND entry_type IN('payment','payment_reversal')),0),
              COALESCE((SELECT SUM(l.amount_cents) FROM supplier_ledger l WHERE l.supplier_id=s.id AND
                (l.entry_type='opening' OR (l.entry_type='purchase_reversal' AND EXISTS(
                  SELECT 1 FROM supplier_invoices i WHERE l.source_type='invoice' AND i.id=l.source_id AND i.kind='opening')))),0)
            FROM suppliers s
            WHERE (@all=1 OR s.active=1) AND (@q IS NULL OR s.name LIKE @q OR s.phone LIKE @q) ORDER BY s.name
            """,
            x => new SupplierRow(x.GetInt64(0), x.GetString(1), x.IsDBNull(2) ? null : x.GetString(2), x.IsDBNull(3) ? null : x.GetString(3),
                x.IsDBNull(4) ? null : x.GetString(4), x.GetInt64(5) == 1, Money.FromCents(x.GetInt64(6)), Money.FromCents(x.GetInt64(7)),
                Money.FromCents(x.GetInt64(8)), 0m, Money.FromCents(x.GetInt64(9))),
            ("@all", includeArchived ? 1 : 0), ("@q", like))
            .Select(r => r with { Overdue = Invoices(c, r.Id, null, null, null, today).Where(i => i.Overdue).Sum(i => i.Outstanding) }).ToList();
    }

    public SupplierRow? Get(long supplierId) => List(null, true).FirstOrDefault(s => s.Id == supplierId);

    // ---------------- purchases ----------------

    static (string? Name, string? Size, string? Color) ProductInfo(SqliteConnection c, long id) =>
        Sql.Query(c, "SELECT name,size,color_name_en FROM products WHERE id=@i",
            x => (Name: (string?)x.GetString(0), Size: x.IsDBNull(1) ? null : x.GetString(1), Color: x.IsDBNull(2) ? null : x.GetString(2)), ("@i", id)).FirstOrDefault();

    /// <summary>Posts a purchase invoice: stock, payable, optional initial payment and cash effect, all in one transaction.</summary>
    public PurchaseResult PostPurchase(PostPurchaseRequest r)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, r.ActorId);
            var dup = Sql.Query(c, "SELECT id,number,total_cents FROM supplier_invoices WHERE operation_id=@o",
                x => (Id: x.GetInt64(0), Number: x.GetString(1), Total: x.GetInt64(2)), ("@o", r.OperationId.ToString("N"))).FirstOrDefault();
            if (dup.Number is not null)
            {
                var paid = PaidOn(c, dup.Id);
                return new PurchaseResult(dup.Id, dup.Number, Money.FromCents(dup.Total), Money.FromCents(paid), Money.FromCents(dup.Total - paid), true);
            }

            Common.RequireOwner(c, r.ActorId);
            RequireSupplier(c, r.SupplierId);
            ValidateOrderReceipt(c, r);
            InstallmentPlans.CheckDate(r.InvoiceDate);
            if (r.DueDate is not null) InstallmentPlans.CheckDate(r.DueDate);
            if (r.Lines.Count == 0) throw new PosException(PosError.EmptyCart, "A purchase needs at least one line.");

            var prepared = new List<(PurchaseLine L, long LineCents, string Desc)>();
            foreach (var l in r.Lines)
            {
                if (l.Quantity <= 0) throw new PosException(PosError.InvalidQuantity, "Received quantity must be positive.");
                if (l.UnitCost < 0) throw new PosException(PosError.InvalidPrice, "Unit cost cannot be negative.");
                Money.ToMilli(l.Quantity);
                var info = ProductInfo(c, l.ProductId);
                if (info.Name is null) throw new PosException(PosError.ProductUnavailable, "Product not found.");
                var desc = l.Description ?? string.Join(" ", new[] { info.Name, info.Size, info.Color }.Where(s => !string.IsNullOrWhiteSpace(s)));
                prepared.Add((l, Money.ToCents(l.Quantity * l.UnitCost), desc));
            }
            var total = prepared.Sum(p => p.LineCents);
            if (total <= 0) throw new PosException(PosError.InvalidPrice, "The purchase total must be positive.");
            var initial = Money.ToCents(r.InitialPayment);
            var creditToUse = Math.Min(total, Credits(c, r.SupplierId).Sum(x => x.Left));
            if (initial < 0 || initial > total - creditToUse) throw new PosException(PosError.PaymentMismatch, "The initial payment cannot exceed the invoice total after supplier credit.");

            var stamp = Common.Stamp(clock);
            var number = Common.NextNumber(c, "P");
            var supplierName = Sql.Scalar<string>(c, "SELECT name FROM suppliers WHERE id=@i", ("@i", r.SupplierId));
            var invId = Sql.Insert(c,
                """
                INSERT INTO supplier_invoices(number,operation_id,supplier_id,kind,supplier_ref,invoice_date,due_date,total_cents,notes,actor_id,created_at)
                VALUES(@n,@o,@s,'purchase',@ref,@d,@due,@tot,@nt,@a,@t)
                """,
                ("@n", number), ("@o", r.OperationId.ToString("N")), ("@s", r.SupplierId), ("@ref", r.SupplierRef), ("@d", r.InvoiceDate),
                ("@due", r.DueDate), ("@tot", total), ("@nt", r.Notes), ("@a", r.ActorId), ("@t", stamp));
            FaultInjector?.Invoke("after_invoice_insert");

            foreach (var (l, lineCents, desc) in prepared)
            {
                // Same stock/cost rules as a normal receipt: movement at the invoice cost, moving weighted average updated.
                var avg = Money.FromCost4(Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", l.ProductId)));
                var onHand = Common.OnHand(c, l.ProductId);
                var newAvg = CostCalculator.WeightedAverage(onHand, avg, l.Quantity, l.UnitCost);
                var receiptId = Sql.Insert(c,
                    """
                    INSERT INTO stock_receipts(product_id,qty_milli,unit_cost_x10000,supplier,reference,document_date,posted_at,actor_id,shift_id)
                    VALUES(@p,@q,@u,@s,@r,@d,@t,@a,NULL)
                    """,
                    ("@p", l.ProductId), ("@q", Money.ToMilli(l.Quantity)), ("@u", Money.ToCost4(l.UnitCost)), ("@s", supplierName),
                    ("@r", number), ("@d", r.InvoiceDate), ("@t", stamp), ("@a", r.ActorId));
                var lotId = StockTracking.Receive(c, l.ProductId, l.LocationId, l.Batch, l.Expiry, Money.ToMilli(l.Quantity), Money.ToCost4(l.UnitCost), "receipt", receiptId, r.ActorId, stamp);
                Sql.Exec(c, "UPDATE products SET avg_cost_x10000=@v WHERE id=@p", ("@v", Money.ToCost4(newAvg)), ("@p", l.ProductId));
                var invoiceLineId = Sql.Insert(c,
                    "INSERT INTO supplier_invoice_lines(invoice_id,product_id,description,qty_milli,unit_cost_x10000,line_cents,receipt_id) VALUES(@i,@p,@d,@q,@u,@lc,@rc)",
                    ("@i", invId), ("@p", l.ProductId), ("@d", desc), ("@q", Money.ToMilli(l.Quantity)), ("@u", Money.ToCost4(l.UnitCost)), ("@lc", lineCents), ("@rc", receiptId));
                Sql.Exec(c, "UPDATE supplier_invoice_lines SET location_id=@l,lot_id=@lot WHERE id=@i", ("@l", l.LocationId), ("@lot", lotId), ("@i", invoiceLineId));
                if (l.OrderLineId is long orderLine)
                    Sql.Exec(c, "INSERT INTO purchase_order_receipts(order_line_id,invoice_line_id,qty_milli) VALUES(@l,@i,@q)",
                        ("@l", orderLine), ("@i", invoiceLineId), ("@q", Money.ToMilli(l.Quantity)));
            }
            FaultInjector?.Invoke("after_stock");

            Sql.Exec(c, "INSERT INTO supplier_ledger(supplier_id,entry_type,amount_cents,source_type,source_id,entry_date,created_at) VALUES(@s,'purchase',@a,'invoice',@i,@d,@t)",
                ("@s", r.SupplierId), ("@a", total), ("@i", invId), ("@d", r.InvoiceDate), ("@t", stamp));

            ApplyAvailableCredits(c, r.SupplierId, stamp);
            if (initial > 0)
                WritePayment(c, r.OperationId, "initial", r.ActorId, r.SupplierId, r.InvoiceDate, initial, r.Method, r.PaymentReference, "Paid on receipt",
                    r.ShiftId, [(invId, initial)], stamp,r.MethodId,r.CardConfirmation);
            ApplyAvailableCredits(c, r.SupplierId, stamp);
            FaultInjector?.Invoke("after_payment");

            Common.Audit(c, clock, r.ActorId, null, "supplier.purchase", number, null, $"supplier={r.SupplierId};total={Money.FromCents(total)}");
            var settled = PaidOn(c, invId);
            return new PurchaseResult(invId, number, Money.FromCents(total), Money.FromCents(settled), Money.FromCents(total - settled), false);
        });
    }

    /// <summary>Owner-only: an amount owed to the supplier from before Dukkan. Creates no stock and no purchase.</summary>
    public long AddOpeningBalance(Guid operationId, long ownerId, long supplierId, decimal amount, string? dueDate = null, string? note = null, string? date = null)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            var dup = Sql.Scalar<long?>(c, "SELECT id FROM supplier_invoices WHERE operation_id=@o", ("@o", operationId.ToString("N")));
            if (dup is long d) return d;
            Common.RequireOwner(c, ownerId);
            RequireSupplier(c, supplierId);
            var cents = Money.ToCents(amount);
            if (cents <= 0) throw new PosException(PosError.InvalidPrice, "The opening amount must be positive.");
            var day = date ?? Common.Today(clock);
            InstallmentPlans.CheckDate(day);
            if (dueDate is not null) InstallmentPlans.CheckDate(dueDate);
            var stamp = Common.Stamp(clock);
            var number = Common.NextNumber(c, "PO");
            var id = Sql.Insert(c,
                """
                INSERT INTO supplier_invoices(number,operation_id,supplier_id,kind,invoice_date,due_date,total_cents,notes,actor_id,created_at)
                VALUES(@n,@o,@s,'opening',@d,@due,@a,@nt,@u,@t)
                """,
                ("@n", number), ("@o", operationId.ToString("N")), ("@s", supplierId), ("@d", day), ("@due", dueDate), ("@a", cents), ("@nt", note), ("@u", ownerId), ("@t", stamp));
            Sql.Exec(c, "INSERT INTO supplier_ledger(supplier_id,entry_type,amount_cents,source_type,source_id,entry_date,created_at) VALUES(@s,'opening',@a,'invoice',@i,@d,@t)",
                ("@s", supplierId), ("@a", cents), ("@i", id), ("@d", day), ("@t", stamp));
            Common.Audit(c, clock, ownerId, null, "supplier.opening_balance", supplierId.ToString(), note, $"amount={amount}");
            ApplyAvailableCredits(c, supplierId, stamp);
            return id;
        });
    }

    // ---------------- payments ----------------

    static void RequireSupplier(SqliteConnection c, long id)
    {
        if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM suppliers WHERE id=@i AND active=1", ("@i", id)) == 0)
            throw new PosException(PosError.NotFound, "Supplier not found or archived.");
    }

    static long PaidOn(SqliteConnection c, long invoiceId) =>
        Sql.Scalar<long>(c,
            """
            SELECT (SELECT COALESCE(SUM(a.amount_cents),0) FROM supplier_payment_allocations a WHERE a.invoice_id=@i
              AND a.payment_id NOT IN(SELECT target_id FROM supplier_reversals WHERE target_type='payment'))
            + (SELECT COALESCE(SUM(a.amount_cents),0) FROM supplier_credit_allocations a WHERE a.invoice_id=@i
              AND a.id NOT IN(SELECT allocation_id FROM supplier_credit_allocation_reversals)
              AND a.return_id NOT IN(SELECT return_id FROM supplier_return_reversals))
            """,
            ("@i", invoiceId));

    sealed record OpenInvoice(long Id, string Date, string? Due, long Left);

    static List<OpenInvoice> OpenInvoices(SqliteConnection c, long supplierId) =>
        Sql.Query(c,
            """
            SELECT i.id,i.invoice_date,i.due_date,i.total_cents FROM supplier_invoices i
            WHERE i.supplier_id=@s AND i.id NOT IN(SELECT target_id FROM supplier_reversals WHERE target_type='invoice')
            ORDER BY i.invoice_date,i.id
            """,
            x => (Id: x.GetInt64(0), Date: x.GetString(1), Due: x.IsDBNull(2) ? null : x.GetString(2), Total: x.GetInt64(3)), ("@s", supplierId))
            .Select(i => new OpenInvoice(i.Id, i.Date, i.Due, i.Total - PaidOn(c, i.Id))).Where(i => i.Left > 0).ToList();

    /// <summary>Default allocation: oldest outstanding invoice first.</summary>
    public IReadOnlyList<SupplierAllocation> Propose(long supplierId, decimal amount)
    {
        using var c = db.Open();
        return Allocate(OpenInvoices(c, supplierId), Money.ToCents(amount)).Select(a => new SupplierAllocation(a.Id, Money.FromCents(a.Cents))).ToList();
    }

    static List<(long Id, long Cents)> Allocate(List<OpenInvoice> open, long cents)
    {
        if (cents > open.Sum(o => o.Left)) throw new PosException(PosError.PaymentMismatch, "The payment is more than the amount owed to this supplier.");
        var plan = new List<(long, long)>();
        var rest = cents;
        foreach (var o in open)
        {
            if (rest == 0) break;
            var take = Math.Min(rest, o.Left);
            plan.Add((o.Id, take));
            rest -= take;
        }
        return plan;
    }


    static long WritePayment(SqliteConnection c, Guid operationId, string kind, long actor, long supplierId, string date, long cents, PaymentMethod method,
        string? reference, string? notes, long? shiftId, IReadOnlyList<(long InvoiceId, long Cents)> allocations, string stamp,long? methodId=null,CardConfirmation? cardConfirmation=null)
    {
        var selectedMethod=PaymentMethodService.Resolve(c,method,methodId);
        var card=PaymentMethodService.Confirm(method,Money.FromCents(cents),cardConfirmation);
        var isCash = method == PaymentMethod.Cash;
        if (isCash)
        {
            if (shiftId is not long sh) throw new PosException(PosError.ShiftNotOpen, "A cash payment needs an open shift.");
            Common.RequireOpenShift(c, sh);
            if (cents > Common.ExpectedCashCents(c, sh))
                throw new PosException(PosError.PaymentMismatch, "The drawer does not hold that much recorded cash.");
        }
        var number = Common.NextNumber(c, "SP");
        var payId = Sql.Insert(c,
            """
            INSERT INTO supplier_payments(number,operation_id,supplier_id,pay_date,amount_cents,method,reference,notes,shift_id,actor_id,created_at)
            VALUES(@n,@o,@s,@d,@a,@m,@r,@nt,@sh,@u,@t)
            """,
            ("@n", number), ("@o", kind == "initial" ? operationId.ToString("N") + "-i" : operationId.ToString("N")), ("@s", supplierId), ("@d", date),
            ("@a", cents), ("@m", method.ToString().ToLowerInvariant()), ("@r", reference), ("@nt", notes), ("@sh", shiftId),
            ("@u", actor), ("@t", stamp));
        PaymentMethodService.Snapshot(c,"supplier_payments",payId,selectedMethod,card);
        foreach (var (inv, take) in allocations)
            Sql.Exec(c, "INSERT INTO supplier_payment_allocations(payment_id,invoice_id,amount_cents) VALUES(@p,@i,@a)", ("@p", payId), ("@i", inv), ("@a", take));
        Sql.Exec(c, "INSERT INTO supplier_ledger(supplier_id,entry_type,amount_cents,source_type,source_id,entry_date,created_at) VALUES(@s,'payment',@a,'payment',@p,@d,@t)",
            ("@s", supplierId), ("@a", -cents), ("@p", payId), ("@d", date), ("@t", stamp));
        if (isCash)
            Sql.Exec(c, "INSERT INTO cash_movements(shift_id,kind,amount_cents,source_type,source_id,created_at) VALUES(@sh,'supplier_payment',@a,'supplier_payment',@p,@t)",
                ("@sh", shiftId), ("@a", -cents), ("@p", payId), ("@t", stamp));
        return payId;
    }

    /// <summary>Owner-only. Every payment is fully allocated to invoices and may not exceed the amount owed. No stock is touched.</summary>
    public SupplierPayResult Pay(SupplierPayRequest r)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, r.ActorId);
            var dup = Sql.Query(c, "SELECT id,number FROM supplier_payments WHERE operation_id=@o", x => (Id: x.GetInt64(0), Number: x.GetString(1)),
                ("@o", r.OperationId.ToString("N"))).FirstOrDefault();
            if (dup.Number is not null)
                return new SupplierPayResult(dup.Id, dup.Number, Sql.Query(c, "SELECT invoice_id,amount_cents FROM supplier_payment_allocations WHERE payment_id=@p ORDER BY id",
                    x => new SupplierAllocation(x.GetInt64(0), Money.FromCents(x.GetInt64(1))), ("@p", dup.Id)), true);

            Common.RequireOwner(c, r.ActorId);
            RequireSupplier(c, r.SupplierId);
            ApplyAvailableCredits(c, r.SupplierId, Common.Stamp(clock));
            InstallmentPlans.CheckDate(r.PayDate);
            var cents = Money.ToCents(r.Amount);
            if (cents <= 0) throw new PosException(PosError.PaymentMismatch, "The payment must be positive.");
            var open = OpenInvoices(c, r.SupplierId);
            if (cents > open.Sum(o => o.Left))
                throw new PosException(PosError.PaymentMismatch, "The payment is more than the amount owed to this supplier.");

            List<(long Id, long Cents)> plan;
            if (r.Allocations is { Count: > 0 })
            {
                plan = [];
                foreach (var a in r.Allocations)
                {
                    var ac = Money.ToCents(a.Amount);
                    var inv = open.FirstOrDefault(o => o.Id == a.InvoiceId);
                    if (inv is null || ac <= 0 || ac > inv.Left) throw new PosException(PosError.PaymentMismatch, "Invalid allocation.");
                    plan.Add((a.InvoiceId, ac));
                }
                if (plan.GroupBy(p => p.Id).Any(g => g.Sum(p => p.Cents) > open.First(o => o.Id == g.Key).Left))
                    throw new PosException(PosError.PaymentMismatch, "An invoice is allocated more than it is owed.");
                if (plan.Sum(p => p.Cents) != cents) throw new PosException(PosError.PaymentMismatch, "The allocations must add up to the payment.");
            }
            else plan = Allocate(open, cents);

            var stamp = Common.Stamp(clock);
            var payId = WritePayment(c, r.OperationId, "later", r.ActorId, r.SupplierId, r.PayDate, cents, r.Method, r.Reference, r.Notes, r.ShiftId, plan, stamp,r.MethodId,r.CardConfirmation);
            FaultInjector?.Invoke("after_payment");
            var number = Sql.Scalar<string>(c, "SELECT number FROM supplier_payments WHERE id=@i", ("@i", payId))!;
            Common.Audit(c, clock, r.ActorId, null, "supplier.payment", number, null, $"supplier={r.SupplierId};amount={r.Amount}");
            return new SupplierPayResult(payId, number, plan.Select(p => new SupplierAllocation(p.Id, Money.FromCents(p.Cents))).ToList(), false);
        });
    }

    // ---------------- reversals ----------------

    /// <summary>Owner-only. Restores the invoice balances and reverses the linked cash movement, once.</summary>
    public void ReversePayment(long ownerId, long paymentId, string reason, long? shiftId = null)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (string.IsNullOrWhiteSpace(reason)) throw new PosException(PosError.InvalidDiscount, "A reason is required.");
            var p = Sql.Query(c, "SELECT supplier_id,amount_cents,method,pay_date FROM supplier_payments WHERE id=@i",
                x => (Supplier: x.GetInt64(0), Cents: x.GetInt64(1), Method: x.GetString(2), Date: x.GetString(3)), ("@i", paymentId)).FirstOrDefault();
            if (p.Cents == 0) throw new PosException(PosError.NotFound, "Payment not found.");
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM supplier_reversals WHERE target_type='payment' AND target_id=@i", ("@i", paymentId)) > 0)
                throw new PosException(PosError.Duplicate, "This payment was already reversed.");
            var isCash = p.Method == "cash";
            if (isCash)
            {
                if (shiftId is not long sh) throw new PosException(PosError.ShiftNotOpen, "Reversing a cash payment needs an open shift.");
                Common.RequireOpenShift(c, sh);
            }
            var stamp = Common.Stamp(clock);
            Sql.Exec(c, "INSERT INTO supplier_reversals(target_type,target_id,reason,shift_id,actor_id,created_at) VALUES('payment',@i,@r,@s,@a,@t)",
                ("@i", paymentId), ("@r", reason), ("@s", shiftId), ("@a", ownerId), ("@t", stamp));
            Sql.Exec(c, "INSERT INTO supplier_ledger(supplier_id,entry_type,amount_cents,source_type,source_id,entry_date,created_at) VALUES(@s,'payment_reversal',@a,'payment',@p,@d,@t)",
                ("@s", p.Supplier), ("@a", p.Cents), ("@p", paymentId), ("@d", Common.Today(clock)), ("@t", stamp));
            if (isCash)
                Sql.Exec(c, "INSERT INTO cash_movements(shift_id,kind,amount_cents,source_type,source_id,created_at) VALUES(@sh,'supplier_payment_reversal',@a,'supplier_payment',@p,@t)",
                    ("@sh", shiftId), ("@a", p.Cents), ("@p", paymentId), ("@t", stamp));
            FaultInjector?.Invoke("after_reversal");
            ApplyAvailableCredits(c, p.Supplier, stamp);
            Common.Audit(c, clock, ownerId, null, "supplier.payment_reverse", paymentId.ToString(), reason);
            return 0;
        });
    }

    /// <summary>Owner-only. Blocked while active payments exist or if it would take stock below zero.</summary>
    public void ReversePurchase(long ownerId, long invoiceId, string reason)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (string.IsNullOrWhiteSpace(reason)) throw new PosException(PosError.InvalidDiscount, "A reason is required.");
            var inv = Sql.Query(c, "SELECT supplier_id,total_cents,number,kind FROM supplier_invoices WHERE id=@i",
                x => (Supplier: x.GetInt64(0), Total: x.GetInt64(1), Number: x.GetString(2), Kind: x.GetString(3)), ("@i", invoiceId)).FirstOrDefault();
            if (inv.Number is null) throw new PosException(PosError.NotFound, "Invoice not found.");
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM supplier_reversals WHERE target_type='invoice' AND target_id=@i", ("@i", invoiceId)) > 0)
                throw new PosException(PosError.Duplicate, "This invoice was already reversed.");
            if (Sql.Scalar<long>(c, """
                SELECT COALESCE(SUM(amount_cents),0) FROM supplier_payment_allocations WHERE invoice_id=@i
                AND payment_id NOT IN(SELECT target_id FROM supplier_reversals WHERE target_type='payment')
                """, ("@i", invoiceId)) > 0)
                throw new PosException(PosError.PermissionDenied, "Reverse the payments on this invoice first.");
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM supplier_returns WHERE invoice_id=@i AND id NOT IN(SELECT return_id FROM supplier_return_reversals)", ("@i", invoiceId)) > 0)
                throw new PosException(PosError.PermissionDenied, "Reverse the supplier returns first.");

            var lines = Sql.Query(c, "SELECT product_id,qty_milli,unit_cost_x10000,location_id,lot_id FROM supplier_invoice_lines WHERE invoice_id=@i",
                x => (Product: x.GetInt64(0), Qty: x.GetInt64(1), Cost4: x.GetInt64(2), Location: x.GetInt64(3), Lot: x.IsDBNull(4) ? (long?)null : x.GetInt64(4)), ("@i", invoiceId));
            foreach (var g in lines.GroupBy(l => (l.Product,l.Location,l.Lot)))
                if (StockTracking.ExactOnHand(c, g.Key.Product, g.Key.Location, g.Key.Lot) < g.Sum(l => l.Qty))
                    throw new PosException(PosError.InsufficientStock, "Some of this stock has already been sold; the purchase cannot be reversed.");

            var stamp = Common.Stamp(clock);
            Sql.Exec(c, "INSERT INTO supplier_reversals(target_type,target_id,reason,actor_id,created_at) VALUES('invoice',@i,@r,@a,@t)",
                ("@i", invoiceId), ("@r", reason), ("@a", ownerId), ("@t", stamp));
            Sql.Exec(c, """
                INSERT INTO supplier_credit_allocation_reversals(allocation_id,reason,actor_id,created_at)
                SELECT a.id,@r,@u,@t FROM supplier_credit_allocations a WHERE a.invoice_id=@i
                  AND a.id NOT IN(SELECT allocation_id FROM supplier_credit_allocation_reversals)
                """, ("@r", reason), ("@u", ownerId), ("@t", stamp), ("@i", invoiceId));
            foreach (var l in lines)
            {
                var qty = Money.FromMilli(l.Qty);
                var onHand = Common.OnHand(c, l.Product);
                var avg = Money.FromCost4(Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", l.Product)));
                StockTracking.Move(c, l.Product, l.Location, l.Lot, -l.Qty, l.Cost4, "purchase_reversal", invoiceId, ownerId, stamp, reason);
                if (onHand > qty)   // remove the received value from the moving average; keep it when stock hits zero
                {
                    var newAvg = (onHand * avg - qty * Money.FromCost4(l.Cost4)) / (onHand - qty);
                    Sql.Exec(c, "UPDATE products SET avg_cost_x10000=@v WHERE id=@p", ("@v", Money.ToCost4(Math.Max(0m, newAvg))), ("@p", l.Product));
                }
            }
            Sql.Exec(c, "INSERT INTO supplier_ledger(supplier_id,entry_type,amount_cents,source_type,source_id,entry_date,created_at) VALUES(@s,'purchase_reversal',@a,'invoice',@i,@d,@t)",
                ("@s", inv.Supplier), ("@a", -inv.Total), ("@i", invoiceId), ("@d", Common.Today(clock)), ("@t", stamp));
            Common.Audit(c, clock, ownerId, null, "supplier.purchase_reverse", inv.Number, reason);
            ApplyAvailableCredits(c, inv.Supplier, stamp);
            return 0;
        });
    }

    // ---------------- reading ----------------

    static List<SupplierInvoiceRow> Invoices(SqliteConnection c, long? supplierId, string? from, string? to, string? status, string today)
    {
        var rows = Sql.Query(c,
            """
            SELECT i.id,i.supplier_id,s.name,i.number,i.kind,i.supplier_ref,i.invoice_date,i.due_date,i.total_cents,
              EXISTS(SELECT 1 FROM supplier_reversals r WHERE r.target_type='invoice' AND r.target_id=i.id)
            FROM supplier_invoices i JOIN suppliers s ON s.id=i.supplier_id
            WHERE (@s IS NULL OR i.supplier_id=@s) AND (@f IS NULL OR i.invoice_date>=@f) AND (@t IS NULL OR i.invoice_date<=@t)
            ORDER BY i.invoice_date DESC,i.id DESC
            """,
            x => (Id: x.GetInt64(0), Sup: x.GetInt64(1), Name: x.GetString(2), Number: x.GetString(3), Kind: x.GetString(4), Ref: x.IsDBNull(5) ? null : x.GetString(5),
                  Date: x.GetString(6), Due: x.IsDBNull(7) ? null : x.GetString(7), Total: x.GetInt64(8), Rev: x.GetInt64(9) == 1),
            ("@s", supplierId), ("@f", from), ("@t", to));
        var list = new List<SupplierInvoiceRow>();
        foreach (var i in rows)
        {
            var paid = i.Rev ? 0 : PaidOn(c, i.Id);
            var left = i.Rev ? 0 : i.Total - paid;
            var st = i.Rev ? "reversed" : left == 0 ? "paid" : paid > 0 ? "partial" : "unpaid";
            var overdue = left > 0 && i.Due is not null && string.CompareOrdinal(i.Due, today) < 0;
            if (status is not null && status != st && !(status == "overdue" && overdue) && !(status == "outstanding" && left > 0)) continue;
            list.Add(new SupplierInvoiceRow(i.Id, i.Sup, i.Name, i.Number, i.Kind, i.Ref, i.Date, i.Due, Money.FromCents(i.Total),
                Money.FromCents(paid), Money.FromCents(left), st, overdue, i.Rev));
        }
        return list;
    }

    /// <summary>Invoices with filters. Status: unpaid | partial | paid | reversed | overdue | outstanding (or null for all).</summary>
    public IReadOnlyList<SupplierInvoiceRow> InvoiceList(long? supplierId = null, string? from = null, string? to = null, string? status = null)
    {
        using var c = db.Open();
        return Invoices(c, supplierId, from, to, status, Common.Today(clock));
    }

    public IReadOnlyList<SupplierInvoiceLineRow> InvoiceLines(long invoiceId)
    {
        using var c = db.Open();
        return Sql.Query(c, "SELECT description,qty_milli,unit_cost_x10000,line_cents FROM supplier_invoice_lines WHERE invoice_id=@i ORDER BY id",
            x => new SupplierInvoiceLineRow(x.GetString(0), Money.FromMilli(x.GetInt64(1)), Money.FromCost4(x.GetInt64(2)), Money.FromCents(x.GetInt64(3))), ("@i", invoiceId));
    }

    public IReadOnlyList<SupplierPaymentRow> Payments(long supplierId)
    {
        using var c = db.Open();
        return Sql.Query(c,
            """
            SELECT p.id,p.number,p.pay_date,p.method,p.amount_cents,p.reference,
              EXISTS(SELECT 1 FROM supplier_reversals r WHERE r.target_type='payment' AND r.target_id=p.id),
              COALESCE((SELECT group_concat(i.number,', ') FROM supplier_payment_allocations a JOIN supplier_invoices i ON i.id=a.invoice_id WHERE a.payment_id=p.id),''),p.method_id,p.method_name_en,p.method_name_ar
            FROM supplier_payments p WHERE p.supplier_id=@s ORDER BY p.id DESC
            """,
            x => new SupplierPaymentRow(x.GetInt64(0), x.GetString(1), x.GetString(2), x.GetString(3), Money.FromCents(x.GetInt64(4)),
                x.IsDBNull(5) ? null : x.GetString(5), x.GetInt64(6) == 1, x.GetString(7),x.IsDBNull(8)?null:x.GetInt64(8),x.IsDBNull(9)?null:x.GetString(9),x.IsDBNull(10)?null:x.GetString(10)), ("@s", supplierId));
    }

    /// <summary>Chronological ledger with running balance: opening, purchases, payments, reversals.</summary>
    public IReadOnlyList<SupplierStatementRow> Statement(long supplierId)
    {
        using var c = db.Open();
        var rows = Sql.Query(c,
            """
            SELECT l.entry_date,l.entry_type,l.amount_cents,
              CASE l.source_type WHEN 'invoice' THEN (SELECT number FROM supplier_invoices WHERE id=l.source_id)
                                 WHEN 'payment' THEN (SELECT number FROM supplier_payments WHERE id=l.source_id)
                                 WHEN 'supplier_return' THEN (SELECT number FROM supplier_returns WHERE id=l.source_id) END
            FROM supplier_ledger l WHERE l.supplier_id=@s ORDER BY l.id
            """,
            x => (Date: x.GetString(0), Type: x.GetString(1), Cents: x.GetInt64(2), Doc: x.IsDBNull(3) ? "" : x.GetString(3)), ("@s", supplierId));
        long running = 0;
        var list = new List<SupplierStatementRow>();
        foreach (var r in rows)
        {
            running += r.Cents;
            list.Add(new SupplierStatementRow(r.Date, r.Type, r.Doc, Money.FromCents(r.Cents), Money.FromCents(running)));
        }
        return list;
    }

    public decimal OutstandingBalance(long supplierId)
    {
        using var c = db.Open();
        return Money.FromCents(Balance(c, supplierId));
    }

    public SupplierDueSummary DueSummary()
    {
        using var c = db.Open();
        var today = Common.Today(clock);
        var open = Invoices(c, null, null, null, "outstanding", today);
        return new SupplierDueSummary(open.Sum(i => i.Outstanding),
            open.Where(i => i.DueDate == today).Sum(i => i.Outstanding),
            open.Where(i => i.Overdue).Sum(i => i.Outstanding));
    }
}
