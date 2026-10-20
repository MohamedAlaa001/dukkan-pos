using LocalCashier.Application;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

// ---------------------------------------------------------------------------------------------------------------------
// Customer credit: installment plans, collections with allocation, reversals, opening balances, due/overdue reporting.
// Debt itself lives in customer_ledger / sales.debt_cents. A schedule only says WHEN existing debt is due; it never adds debt.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Installment plan storage. A plan is immutable; any change creates a new version and supersedes the old one.</summary>
internal static class InstallmentPlans
{
    public const string PaidCte =
        "COALESCE((SELECT SUM(a.amount_cents) FROM installment_allocations a WHERE a.installment_id=i.id AND a.collection_id NOT IN(SELECT collection_id FROM collection_reversals)),0)";

    public sealed record Row(long Id, long SaleId, int Seq, string Due, long Amount, long Carried, long Allocated)
    {
        public long Paid => Carried + Allocated;
        public long Remaining => Amount - Paid;
    }

    public static List<Row> Active(SqliteConnection c, long saleId) =>
        Sql.Query(c,
            $"""
            SELECT i.id,i.sale_id,i.seq,i.due_date,i.amount_cents,i.carried_paid_cents,{PaidCte}
            FROM sale_installments i JOIN installment_plans p ON p.id=i.plan_id
            WHERE p.superseded_at IS NULL AND i.sale_id=@s ORDER BY i.seq
            """,
            x => new Row(x.GetInt64(0), x.GetInt64(1), x.GetInt32(2), x.GetString(3), x.GetInt64(4), x.GetInt64(5), x.GetInt64(6)), ("@s", saleId));

    /// <summary>Checks an arrangement against the unpaid amount. Returns the installment list (null for plain credit).</summary>
    public static IReadOnlyList<(string Due, long Cents)>? Validate(CreditArrangement? a, long debtCents, string today)
    {
        if (a is null) return null;
        if (a.DueDate is not null) CheckDate(a.DueDate);
        if (a.Installments is not { Count: > 0 }) return null;
        var rows = a.Installments.Select(i => { CheckDate(i.DueDate); return (i.DueDate, Cents: Money.ToCents(i.Amount)); }).ToList();
        if (rows.Any(r => r.Cents <= 0)) throw new PosException(PosError.InvalidPrice, "Every installment must be a positive amount.");
        if (rows.Sum(r => r.Cents) != debtCents)
            throw new PosException(PosError.PaymentMismatch, "Installments must add up exactly to the unpaid balance.");
        return rows.OrderBy(r => r.DueDate, StringComparer.Ordinal).ToList();
    }

    public static void CheckDate(string d)
    {
        if (!DateOnly.TryParseExact(d, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new PosException(PosError.InvalidPrice, "Dates must be valid (yyyy-MM-dd).");
    }

    /// <summary>Writes a new plan version for the sale and supersedes the previous one.</summary>
    public static long Create(SqliteConnection c, string stamp, long saleId, string kind, string? reason, long? actor, IReadOnlyList<(string Due, long Amount, long Carried)> rows)
    {
        Sql.Exec(c, "UPDATE installment_plans SET superseded_at=@t WHERE sale_id=@s AND superseded_at IS NULL", ("@t", stamp), ("@s", saleId));
        var version = (Sql.Scalar<long?>(c, "SELECT MAX(version) FROM installment_plans WHERE sale_id=@s", ("@s", saleId)) ?? 0) + 1;
        var planId = Sql.Insert(c, "INSERT INTO installment_plans(sale_id,version,kind,reason,actor_id,created_at) VALUES(@s,@v,@k,@r,@a,@t)",
            ("@s", saleId), ("@v", version), ("@k", kind), ("@r", reason), ("@a", actor), ("@t", stamp));
        var seq = 1;
        foreach (var r in rows.OrderBy(x => x.Due, StringComparer.Ordinal))
            Sql.Exec(c, "INSERT INTO sale_installments(plan_id,sale_id,seq,due_date,amount_cents,carried_paid_cents) VALUES(@p,@s,@q,@d,@a,@cp)",
                ("@p", planId), ("@s", saleId), ("@q", seq++), ("@d", r.Due), ("@a", r.Amount), ("@cp", r.Carried));
        return planId;
    }

    /// <summary>The schedule after a return reduces the debt: latest unpaid installments shrink first. Settled pieces are kept.</summary>
    public static List<(string Due, long Amount, long Carried)> ReducedPlan(IReadOnlyList<Row> active, long reduction)
    {
        var remaining = active.Select(r => r.Remaining).ToArray();
        var left = reduction;
        for (var i = remaining.Length - 1; i >= 0 && left > 0; i--)
        {
            var take = Math.Min(remaining[i], left);
            remaining[i] -= take;
            left -= take;
        }
        var rows = new List<(string, long, long)>();
        for (var i = 0; i < active.Count; i++)
        {
            var paid = active[i].Paid;
            if (remaining[i] == 0 && paid == 0) continue;               // fully cancelled installment
            rows.Add((active[i].Due, paid + remaining[i], paid));
        }
        return rows;
    }

    public static void ReduceLatestUnpaid(SqliteConnection c, string stamp, long saleId, long reduction, long actor, string reason)
    {
        var active = Active(c, saleId);
        if (active.Count == 0) return;
        Create(c, stamp, saleId, "return", reason, actor, ReducedPlan(active, reduction));
    }
}

public sealed record InstallmentRow(long Id, long SaleId, string SaleNumber, int Seq, string DueDate, decimal Amount, decimal Paid, decimal Remaining, string Status, bool Overdue);

public sealed record PlanVersion(int Version, string Kind, string? Reason, string CreatedAt, bool Active, IReadOnlyList<InstallmentRow> Installments);

public sealed record DueItem(long CustomerId, string Customer, string Document, string? DueDate, decimal Remaining, string Status, bool Overdue, long? SaleId, long? InstallmentId);

public sealed record DueSummary(decimal Outstanding, decimal DueTotal, decimal OverdueTotal);

public sealed class InstallmentService(PosDatabase db, IClock clock)
{
    /// <summary>Equal installments from a first due date, monthly. The rounding remainder goes to the last one.</summary>
    public static IReadOnlyList<InstallmentInput> EqualPlan(decimal balance, int count, string firstDue)
    {
        var parts = InstallmentCalculator.EqualSplit(Money.ToCents(balance), count);
        var dates = InstallmentCalculator.MonthlyDates(firstDue, count);
        return parts.Select((p, i) => new InstallmentInput(dates[i], Money.FromCents(p))).ToList();
    }

    static InstallmentRow ToRow(InstallmentPlans.Row r, string number, string today) =>
        new(r.Id, r.SaleId, number, r.Seq, r.Due, Money.FromCents(r.Amount), Money.FromCents(r.Paid), Money.FromCents(r.Remaining),
            InstallmentCalculator.Status(r.Due, r.Paid, r.Remaining, today), InstallmentCalculator.IsOverdue(r.Due, r.Remaining, today));

    public IReadOnlyList<InstallmentRow> Schedule(long saleId)
    {
        using var c = db.Open();
        var number = Sql.Scalar<string>(c, "SELECT number FROM sales WHERE id=@s", ("@s", saleId)) ?? "";
        var today = Common.Today(clock);
        return InstallmentPlans.Active(c, saleId).Select(r => ToRow(r, number, today)).ToList();
    }

    public IReadOnlyList<PlanVersion> History(long saleId)
    {
        using var c = db.Open();
        var number = Sql.Scalar<string>(c, "SELECT number FROM sales WHERE id=@s", ("@s", saleId)) ?? "";
        var today = Common.Today(clock);
        var plans = Sql.Query(c, "SELECT id,version,kind,reason,created_at,superseded_at FROM installment_plans WHERE sale_id=@s ORDER BY version",
            x => (Id: x.GetInt64(0), V: x.GetInt32(1), Kind: x.GetString(2), Reason: x.IsDBNull(3) ? null : x.GetString(3), At: x.GetString(4), Sup: x.IsDBNull(5)), ("@s", saleId));
        return plans.Select(p => new PlanVersion(p.V, p.Kind, p.Reason, p.At, p.Sup,
            Sql.Query(c,
                $"SELECT i.id,i.sale_id,i.seq,i.due_date,i.amount_cents,i.carried_paid_cents,{InstallmentPlans.PaidCte} FROM sale_installments i WHERE i.plan_id=@p ORDER BY i.seq",
                x => ToRow(new InstallmentPlans.Row(x.GetInt64(0), x.GetInt64(1), x.GetInt32(2), x.GetString(3), x.GetInt64(4), x.GetInt64(5), x.GetInt64(6)), number, today),
                ("@p", p.Id)))).ToList();
    }

    /// <summary>What the remaining schedule would look like after a return of this debt value (shown before confirming).</summary>
    public IReadOnlyList<InstallmentRow> PreviewReduction(long saleId, decimal reduction)
    {
        using var c = db.Open();
        var number = Sql.Scalar<string>(c, "SELECT number FROM sales WHERE id=@s", ("@s", saleId)) ?? "";
        var today = Common.Today(clock);
        var active = InstallmentPlans.Active(c, saleId);
        var rows = InstallmentPlans.ReducedPlan(active, Money.ToCents(reduction));
        return rows.Select((r, i) => ToRow(new InstallmentPlans.Row(0, saleId, i + 1, r.Due, r.Amount, r.Carried, 0), number, today)).ToList();
    }

    /// <summary>Owner-only. Replaces the unpaid part of the schedule. Payments, the old plan and the total debt are untouched.</summary>
    public void Reschedule(long ownerId, long saleId, string reason, IReadOnlyList<InstallmentInput> newInstallments)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (string.IsNullOrWhiteSpace(reason)) throw new PosException(PosError.InvalidDiscount, "A reason is required to reschedule.");
            var outstanding = Common.SaleOutstandingCents(c, saleId);
            if (outstanding <= 0) throw new PosException(PosError.PaymentMismatch, "Nothing is owed on this invoice.");
            if (newInstallments.Count == 0) throw new PosException(PosError.InvalidQuantity, "At least one installment is required.");
            var fresh = newInstallments.Select(i => { InstallmentPlans.CheckDate(i.DueDate); return (i.DueDate, Cents: Money.ToCents(i.Amount)); }).ToList();
            if (fresh.Any(f => f.Cents <= 0)) throw new PosException(PosError.InvalidPrice, "Every installment must be a positive amount.");
            if (fresh.Sum(f => f.Cents) != outstanding)
                throw new PosException(PosError.PaymentMismatch, "The new installments must add up exactly to the remaining balance.");

            var rows = new List<(string Due, long Amount, long Carried)>();
            foreach (var old in InstallmentPlans.Active(c, saleId).Where(o => o.Paid > 0))
                rows.Add((old.Due, old.Paid, old.Paid));                       // settled piece keeps its history
            rows.AddRange(fresh.Select(f => (f.DueDate, f.Cents, 0L)));
            InstallmentPlans.Create(c, Common.Stamp(clock), saleId, "reschedule", reason, ownerId, rows);
            Common.Audit(c, clock, ownerId, null, "installments.reschedule", saleId.ToString(), reason, $"remaining={Money.FromCents(outstanding)}");
            return 0;
        });
    }
}

public sealed record DebtAllocation(long? SaleId, long? OpeningId, long? InstallmentId, decimal Amount);

public sealed record CollectRequest(
    Guid OperationId, long CustomerId, decimal Amount, PaymentMethod Method, long ShiftId, long ActorId,
    string? PayDate = null, string? Reference = null, string? Notes = null, IReadOnlyList<DebtAllocation>? Allocations = null, long? MethodId=null,CardConfirmation? CardConfirmation=null);

public sealed record AllocationRequest(long SaleId, decimal Amount);

public sealed record CollectResult(long CollectionId, string Number, IReadOnlyList<AllocationRequest> Allocations, bool AlreadyPosted);

public sealed record DebtSlice(string Kind, long Id, long? InstallmentId, string Document, string? DueDate, decimal Remaining, string Status, bool Overdue);

public sealed record CollectionRow(long Id, string Number, string PayDate, string Method, decimal Amount, string? Reference, bool Reversed, string Allocated,
    long? MethodId=null,string? MethodNameEn=null,string? MethodNameAr=null);

public sealed class CollectionService(PosDatabase db, IClock clock)
{
    sealed record Slice(string Kind, long Id, long? InstallmentId, string Document, string Created, string? Due, long Left, long Paid);

    static List<Slice> OpenSlices(SqliteConnection c, long customerId)
    {
        var slices = new List<Slice>();
        foreach (var o in Sql.Query(c,
            """
            SELECT o.id,o.entry_date,o.amount_cents - COALESCE((SELECT SUM(a.amount_cents) FROM collection_opening_allocations a
              WHERE a.opening_id=o.id AND a.collection_id NOT IN(SELECT collection_id FROM collection_reversals)),0)
            FROM customer_opening_balances o WHERE o.customer_id=@c ORDER BY o.entry_date,o.id
            """, x => (Id: x.GetInt64(0), Date: x.GetString(1), Left: x.GetInt64(2)), ("@c", customerId)))
            if (o.Left > 0) slices.Add(new Slice("opening", o.Id, null, "OPEN", o.Date, null, o.Left, 0));

        foreach (var s in Sql.Query(c, "SELECT id,number,created_at,due_date FROM sales WHERE customer_id=@c AND debt_cents>0 AND status='posted' ORDER BY created_at,id",
            x => (Id: x.GetInt64(0), Number: x.GetString(1), At: x.GetString(2), Due: x.IsDBNull(3) ? null : x.GetString(3)), ("@c", customerId)))
        {
            var left = Common.SaleOutstandingCents(c, s.Id);
            if (left <= 0) continue;
            var plan = InstallmentPlans.Active(c, s.Id);
            if (plan.Count == 0) slices.Add(new Slice("sale", s.Id, null, s.Number, s.At, s.Due, left, 0));
            else foreach (var i in plan.Where(i => i.Remaining > 0)) slices.Add(new Slice("sale", s.Id, i.Id, s.Number, s.At, i.Due, i.Remaining, i.Paid));
        }
        // Earliest due amounts first, then amounts with no due date, oldest first.
        return slices.OrderBy(x => x.Due ?? "9999-12-31", StringComparer.Ordinal).ThenBy(x => x.Created, StringComparer.Ordinal).ThenBy(x => x.Id).ThenBy(x => x.InstallmentId).ToList();
    }

    /// <summary>Everything the customer still owes, in the default settlement order.</summary>
    public IReadOnlyList<DebtSlice> OpenDebts(long customerId)
    {
        using var c = db.Open();
        var today = Common.Today(clock);
        return OpenSlices(c, customerId).Select(s => new DebtSlice(s.Kind, s.Id, s.InstallmentId, s.Document, s.Due, Money.FromCents(s.Left),
            s.Due is null ? "open" : InstallmentCalculator.Status(s.Due, s.Paid, s.Left, today),
            s.Due is not null && InstallmentCalculator.IsOverdue(s.Due, s.Left, today))).ToList();
    }

    /// <summary>Proposed default allocation for an amount (earliest due first, then oldest invoices).</summary>
    public IReadOnlyList<DebtAllocation> Propose(long customerId, decimal amount)
    {
        using var c = db.Open();
        return Allocate(OpenSlices(c, customerId), Money.ToCents(amount));
    }

    static List<DebtAllocation> Allocate(List<Slice> slices, long cents)
    {
        if (cents > slices.Sum(s => s.Left)) throw new PosException(PosError.PaymentMismatch, "Collection exceeds the outstanding debt.");
        var plan = new List<DebtAllocation>();
        var remaining = cents;
        foreach (var s in slices)
        {
            if (remaining == 0) break;
            var take = Math.Min(s.Left, remaining);
            plan.Add(new DebtAllocation(s.Kind == "sale" ? s.Id : null, s.Kind == "opening" ? s.Id : null, s.InstallmentId, Money.FromCents(take)));
            remaining -= take;
        }
        return plan;
    }

    public decimal Outstanding(long saleId)
    {
        using var c = db.Open();
        return Money.FromCents(Common.SaleOutstandingCents(c, saleId));
    }

    /// <summary>Collects an existing debt (compatibility overload: allocation per invoice).</summary>
    public CollectResult Collect(Guid operationId, long customerId, decimal amount, PaymentMethod method, long shiftId, long actorId,
        IReadOnlyList<AllocationRequest>? allocations = null) =>
        Collect(new CollectRequest(operationId, customerId, amount, method, shiftId, actorId,
            Allocations: allocations?.Select(a => new DebtAllocation(a.SaleId, null, null, a.Amount)).ToList()));

    public CollectResult Collect(CollectRequest r)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            var existing = Sql.Query(c, "SELECT id,number FROM collections WHERE operation_id=@o", x => (x.GetInt64(0), x.GetString(1)),
                ("@o", r.OperationId.ToString("N")));
            if (existing.Count == 1)
                return new CollectResult(existing[0].Item1, existing[0].Item2, Sql.Query(c,
                    "SELECT sale_id,amount_cents FROM collection_allocations WHERE collection_id=@i ORDER BY id",
                    x => new AllocationRequest(x.GetInt64(0), Money.FromCents(x.GetInt64(1))), ("@i", existing[0].Item1)), true);

            var role = Common.RequireActive(c, r.ActorId);
            Common.RequireOpenShift(c, r.ShiftId);
            if (r.Amount <= 0) throw new PosException(PosError.PaymentMismatch, "Collection amount must be positive.");
            var cents = Money.ToCents(r.Amount);
            var method=PaymentMethodService.Resolve(c,r.Method,r.MethodId);
            var card=PaymentMethodService.Confirm(r.Method,r.Amount,r.CardConfirmation);
            if (r.PayDate is not null) InstallmentPlans.CheckDate(r.PayDate);

            var slices = OpenSlices(c, r.CustomerId);
            if (cents > slices.Sum(s => s.Left))
                throw new PosException(PosError.PaymentMismatch, "Collection exceeds the outstanding debt.");

            List<DebtAllocation> plan;
            if (r.Allocations is { Count: > 0 })
            {
                if (role != Role.Owner) throw new PosException(PosError.PermissionDenied, "Only the owner can adjust allocation.");
                plan = Expand(c, r.Allocations, slices);
                if (plan.Sum(p => Money.ToCents(p.Amount)) != cents)
                    throw new PosException(PosError.PaymentMismatch, "Allocations must equal the collected amount.");
            }
            else plan = Allocate(slices, cents);

            var stamp = Common.Stamp(clock);
            var number = Common.NextNumber(c, "C");
            var colId = Sql.Insert(c,
                "INSERT INTO collections(number,operation_id,customer_id,shift_id,actor_id,method,amount_cents,created_at,pay_date,reference,notes) VALUES(@n,@o,@c,@sh,@a,@m,@amt,@t,@pd,@rf,@nt)",
                ("@n", number), ("@o", r.OperationId.ToString("N")), ("@c", r.CustomerId), ("@sh", r.ShiftId), ("@a", r.ActorId),
                ("@m", r.Method.ToString().ToLowerInvariant()), ("@amt", cents), ("@t", stamp),
                ("@pd", r.PayDate ?? Common.Today(clock)), ("@rf", r.Reference), ("@nt", r.Notes));
            PaymentMethodService.Snapshot(c,"collections",colId,method,card);

            foreach (var g in plan.Where(p => p.SaleId is not null).GroupBy(p => p.SaleId!.Value))
                Sql.Exec(c, "INSERT INTO collection_allocations(collection_id,sale_id,amount_cents) VALUES(@c,@s,@a)",
                    ("@c", colId), ("@s", g.Key), ("@a", g.Sum(p => Money.ToCents(p.Amount))));
            foreach (var p in plan.Where(p => p.InstallmentId is not null))
                Sql.Exec(c, "INSERT INTO installment_allocations(collection_id,installment_id,amount_cents) VALUES(@c,@i,@a)",
                    ("@c", colId), ("@i", p.InstallmentId), ("@a", Money.ToCents(p.Amount)));
            foreach (var g in plan.Where(p => p.OpeningId is not null).GroupBy(p => p.OpeningId!.Value))
                Sql.Exec(c, "INSERT INTO collection_opening_allocations(collection_id,opening_id,amount_cents) VALUES(@c,@o,@a)",
                    ("@c", colId), ("@o", g.Key), ("@a", g.Sum(p => Money.ToCents(p.Amount))));

            Sql.Exec(c, "INSERT INTO customer_ledger(customer_id,entry_type,amount_cents,source_type,source_id,created_at) VALUES(@c,'collection',@a,'collection',@s,@t)",
                ("@c", r.CustomerId), ("@a", -cents), ("@s", colId), ("@t", stamp));
            if (r.Method == PaymentMethod.Cash)
                Sql.Exec(c, "INSERT INTO cash_movements(shift_id,kind,amount_cents,source_type,source_id,created_at) VALUES(@sh,'debt_collection',@a,'collection',@s,@t)",
                    ("@sh", r.ShiftId), ("@a", cents), ("@s", colId), ("@t", stamp));

            Common.Audit(c, clock, r.ActorId, null, "collection.post", number);
            return new CollectResult(colId, number,
                plan.Where(p => p.SaleId is not null).GroupBy(p => p.SaleId!.Value).Select(g => new AllocationRequest(g.Key, Money.FromCents(g.Sum(p => Money.ToCents(p.Amount))))).ToList(), false);
        });
    }

    /// <summary>Validates an explicit allocation; a sale with an installment plan and no installment given is spread earliest-due first.</summary>
    static List<DebtAllocation> Expand(SqliteConnection c, IReadOnlyList<DebtAllocation> asked, List<Slice> slices)
    {
        var left = slices.ToDictionary(s => (s.Kind, s.Id, s.InstallmentId), s => s.Left);
        var result = new List<DebtAllocation>();
        foreach (var a in asked)
        {
            var cents = Money.ToCents(a.Amount);
            if (cents <= 0) throw new PosException(PosError.PaymentMismatch, "Invalid allocation.");
            if (a.OpeningId is long oid)
            {
                Take(left, ("opening", oid, (long?)null), cents);
                result.Add(a with { SaleId = null, InstallmentId = null, Amount = Money.FromCents(cents) });
            }
            else if (a.SaleId is long sid && a.InstallmentId is long iid)
            {
                Take(left, ("sale", sid, (long?)iid), cents);
                result.Add(a with { OpeningId = null, Amount = Money.FromCents(cents) });
            }
            else if (a.SaleId is long sid2)
            {
                var own = slices.Where(s => s.Kind == "sale" && s.Id == sid2).ToList();
                if (own.Count == 0) throw new PosException(PosError.PaymentMismatch, "Invalid allocation.");
                var rest = cents;
                foreach (var s in own)
                {
                    if (rest == 0) break;
                    var take = Math.Min(rest, left[(s.Kind, s.Id, s.InstallmentId)]);
                    if (take <= 0) continue;
                    Take(left, (s.Kind, s.Id, s.InstallmentId), take);
                    result.Add(new DebtAllocation(sid2, null, s.InstallmentId, Money.FromCents(take)));
                    rest -= take;
                }
                if (rest > 0) throw new PosException(PosError.PaymentMismatch, "Allocation exceeds what is owed on the invoice.");
            }
            else throw new PosException(PosError.PaymentMismatch, "Invalid allocation.");
        }
        return result;
    }

    static void Take(Dictionary<(string, long, long?), long> left, (string, long, long?) key, long cents)
    {
        if (!left.TryGetValue(key, out var l) || cents > l) throw new PosException(PosError.PaymentMismatch, "Allocation exceeds what is owed.");
        left[key] = l - cents;
    }

    /// <summary>Owner-only. Restores the allocations and debt and reverses the cash effect, once.</summary>
    public void Reverse(long ownerId, long collectionId, string reason, long shiftId)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (string.IsNullOrWhiteSpace(reason)) throw new PosException(PosError.InvalidDiscount, "A reason is required.");
            var col = Sql.Query(c, "SELECT customer_id,method,amount_cents FROM collections WHERE id=@i",
                x => (Customer: x.GetInt64(0), Method: x.GetString(1), Cents: x.GetInt64(2)), ("@i", collectionId)).FirstOrDefault();
            if (col.Cents == 0) throw new PosException(PosError.NotFound, "Collection not found.");
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM collection_reversals WHERE collection_id=@i", ("@i", collectionId)) > 0)
                throw new PosException(PosError.Duplicate, "This collection was already reversed.");
            // Do not erase actual money that has already funded a sale refund.
            if(Sql.Scalar<long>(c,"""
                SELECT COUNT(*) FROM collection_allocations a JOIN sales s ON s.id=a.sale_id WHERE a.collection_id=@i
                AND a.amount_cents>s.paid_cents
                  +COALESCE((SELECT SUM(p.amount_cents) FROM collection_allocations p WHERE p.sale_id=s.id AND p.collection_id NOT IN(SELECT collection_id FROM collection_reversals)),0)
                  -COALESCE((SELECT SUM(refund_cents) FROM returns WHERE sale_id=s.id),0)
                """,("@i",collectionId))>0)throw new PosException(PosError.PaymentMismatch,"This collection has funded a refund and cannot be reversed.");
            // A schedule that was changed after this payment (reschedule/return) no longer carries it as a live allocation.
            if (Sql.Scalar<long>(c,
                    """
                    SELECT COUNT(*) FROM installment_allocations a JOIN sale_installments i ON i.id=a.installment_id
                    JOIN installment_plans p ON p.id=i.plan_id WHERE a.collection_id=@i AND p.superseded_at IS NOT NULL
                    """, ("@i", collectionId)) > 0)
                throw new PosException(PosError.PermissionDenied, "The installment schedule changed after this payment, so it cannot be reversed here.");
            Common.RequireOpenShift(c, shiftId);
            var isCash = col.Method == "cash";
            if (isCash && col.Cents > Common.ExpectedCashCents(c, shiftId))
                throw new PosException(PosError.PaymentMismatch, "The drawer does not hold enough recorded cash to reverse this payment.");

            var stamp = Common.Stamp(clock);
            Sql.Exec(c, "INSERT INTO collection_reversals(collection_id,reason,actor_id,shift_id,created_at) VALUES(@i,@r,@a,@s,@t)",
                ("@i", collectionId), ("@r", reason), ("@a", ownerId), ("@s", shiftId), ("@t", stamp));
            Sql.Exec(c, "INSERT INTO customer_ledger(customer_id,entry_type,amount_cents,source_type,source_id,created_at) VALUES(@c,'collection_reversal',@a,'collection',@s,@t)",
                ("@c", col.Customer), ("@a", col.Cents), ("@s", collectionId), ("@t", stamp));
            if (isCash)
                Sql.Exec(c, "INSERT INTO cash_movements(shift_id,kind,amount_cents,source_type,source_id,created_at) VALUES(@sh,'debt_collection_reversal',@a,'collection',@s,@t)",
                    ("@sh", shiftId), ("@a", -col.Cents), ("@s", collectionId), ("@t", stamp));
            Common.Audit(c, clock, ownerId, null, "collection.reverse", collectionId.ToString(), reason);
            return 0;
        });
    }

    public IReadOnlyList<CollectionRow> History(long customerId)
    {
        using var c = db.Open();
        return Sql.Query(c,
            """
            SELECT c.id,c.number,COALESCE(c.pay_date,substr(c.created_at,1,10)),c.method,c.amount_cents,c.reference,
              EXISTS(SELECT 1 FROM collection_reversals r WHERE r.collection_id=c.id),
              COALESCE((SELECT group_concat(s.number||' '||printf('%.2f',a.amount_cents/100.0),', ') FROM collection_allocations a JOIN sales s ON s.id=a.sale_id WHERE a.collection_id=c.id),'')
              || COALESCE((SELECT group_concat('OPEN '||printf('%.2f',o.amount_cents/100.0),', ') FROM collection_opening_allocations o WHERE o.collection_id=c.id),''),c.method_id,c.method_name_en,c.method_name_ar
            FROM collections c WHERE c.customer_id=@c ORDER BY c.id DESC
            """,
            x => new CollectionRow(x.GetInt64(0), x.GetString(1), x.GetString(2), x.GetString(3), Money.FromCents(x.GetInt64(4)),
                x.IsDBNull(5) ? null : x.GetString(5), x.GetInt64(6) == 1, x.GetString(7),x.IsDBNull(8)?null:x.GetInt64(8),x.IsDBNull(9)?null:x.GetString(9),x.IsDBNull(10)?null:x.GetString(10)), ("@c", customerId));
    }
}

public sealed class CustomerCreditService(PosDatabase db, IClock clock)
{
    /// <summary>Owner-only migration entry: an amount a customer already owed. No sale, revenue or stock is created.</summary>
    public long AddOpeningBalance(Guid operationId, long ownerId, long customerId, decimal amount, string? note, string? date = null)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            var dup = Sql.Scalar<long?>(c, "SELECT id FROM customer_opening_balances WHERE operation_id=@o", ("@o", operationId.ToString("N")));
            if (dup is long d) return d;
            Common.RequireOwner(c, ownerId);
            var cents = Money.ToCents(amount);
            if (cents <= 0) throw new PosException(PosError.InvalidPrice, "The opening amount must be positive.");
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM customers WHERE id=@i AND active=1", ("@i", customerId)) == 0)
                throw new PosException(PosError.NotFound, "Customer not found.");
            if (date is not null) InstallmentPlans.CheckDate(date);
            var stamp = Common.Stamp(clock);
            var id = Sql.Insert(c, "INSERT INTO customer_opening_balances(operation_id,customer_id,amount_cents,entry_date,note,actor_id,created_at) VALUES(@o,@c,@a,@d,@n,@u,@t)",
                ("@o", operationId.ToString("N")), ("@c", customerId), ("@a", cents), ("@d", date ?? Common.Today(clock)), ("@n", note), ("@u", ownerId), ("@t", stamp));
            Sql.Exec(c, "INSERT INTO customer_ledger(customer_id,entry_type,amount_cents,source_type,source_id,created_at) VALUES(@c,'opening',@a,'opening',@s,@t)",
                ("@c", customerId), ("@a", cents), ("@s", id), ("@t", stamp));
            Common.Audit(c, clock, ownerId, null, "customer.opening_balance", customerId.ToString(), note, $"amount={amount}");
            return id;
        });
    }

    /// <summary>Credit headroom shown before confirming a credit sale.</summary>
    public (decimal Balance, decimal Limit, decimal Available, bool CreditEnabled) Position(long customerId)
    {
        using var c = db.Open();
        var cu = Sql.Query(c, "SELECT credit_enabled,credit_limit_cents FROM customers WHERE id=@i",
            x => (En: x.GetInt64(0) == 1, Limit: x.GetInt64(1)), ("@i", customerId)).FirstOrDefault();
        var bal = Common.CustomerBalanceCents(c, customerId);
        return (Money.FromCents(bal), Money.FromCents(cu.Limit), Money.FromCents(Math.Max(0, cu.Limit - bal)), cu.En);
    }

    /// <summary>Due and overdue amounts across customers. Overdue is always a subset of due-or-earlier unpaid portions.</summary>
    public IReadOnlyList<DueItem> DueItems(long? customerId = null, bool overdueOnly = false)
    {
        using var c = db.Open();
        var today = Common.Today(clock);
        var coll = new CollectionService(db, clock);
        var items = new List<DueItem>();
        var customers = Sql.Query(c, "SELECT id,name FROM customers WHERE (@c IS NULL OR id=@c) ORDER BY name", x => (Id: x.GetInt64(0), Name: x.GetString(1)), ("@c", customerId));
        foreach (var cu in customers)
            foreach (var s in coll.OpenDebts(cu.Id))
                items.Add(new DueItem(cu.Id, cu.Name, s.Document, s.DueDate, s.Remaining, s.Status, s.Overdue,
                    s.Kind == "sale" ? s.Id : null, s.InstallmentId));
        return items.Where(i => !overdueOnly || i.Overdue).OrderBy(i => i.DueDate ?? "9999-12-31", StringComparer.Ordinal).ToList();
    }

    public DueSummary Summary(long? customerId = null)
    {
        var items = DueItems(customerId);
        var today = Common.Today(clock);
        return new DueSummary(items.Sum(i => i.Remaining),
            items.Where(i => i.DueDate is not null && string.CompareOrdinal(i.DueDate, today) <= 0).Sum(i => i.Remaining),
            items.Where(i => i.Overdue).Sum(i => i.Remaining));
    }
}
