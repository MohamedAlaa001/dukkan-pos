using LocalCashier.Application;
using LocalCashier.Domain;

namespace LocalCashier.Persistence;

public sealed record ShiftCloseResult(decimal Expected, decimal Counted, decimal Variance);

public sealed class CashService(PosDatabase db, IClock clock)
{
    /// <summary>Owner cash-in or cash-out of the drawer. These are not sales or expenses.</summary>
    public void RecordOwnerCash(long ownerId, long shiftId, bool cashIn, decimal amount, string reason)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            Common.RequireOpenShift(c, shiftId);
            var cents = Money.ToCents(amount);
            if (cents <= 0) throw new PosException(PosError.PaymentMismatch, "Amount must be positive.");
            if (!cashIn && cents > Common.ExpectedCashCents(c, shiftId))
                throw new PosException(PosError.PaymentMismatch, "The drawer does not hold that much recorded cash.");
            Sql.Exec(c, "INSERT INTO cash_movements(shift_id,kind,amount_cents,source_type,created_at) VALUES(@s,@k,@a,'owner',@t)",
                ("@s", shiftId), ("@k", cashIn ? "owner_in" : "owner_out"), ("@a", cashIn ? cents : -cents), ("@t", Common.Stamp(clock)));
            Common.Audit(c, clock, ownerId, null, cashIn ? "cash.owner_in" : "cash.owner_out", shiftId.ToString(), reason);
            return 0;
        });
    }

    /// <summary>Operating expense. Register-funded expenses need an open shift and reduce drawer cash.</summary>
    public long RecordExpense(long ownerId, string date, string category, decimal amount, string? description, bool fromRegister, long? shiftId)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            var cents = Money.ToCents(amount);
            if (cents <= 0) throw new PosException(PosError.PaymentMismatch, "Amount must be positive.");
            if (fromRegister)
            {
                if (shiftId is null) throw new PosException(PosError.ShiftNotOpen, "A register-funded expense needs an open shift.");
                Common.RequireOpenShift(c, shiftId.Value);
            }

            var stamp = Common.Stamp(clock);
            var id = Sql.Insert(c,
                "INSERT INTO expenses(expense_date,category,amount_cents,description,source,shift_id,actor_id,created_at) VALUES(@d,@cat,@a,@desc,@src,@sh,@act,@t)",
                ("@d", date), ("@cat", category), ("@a", cents), ("@desc", description), ("@src", fromRegister ? "register" : "external"),
                ("@sh", fromRegister ? shiftId : null), ("@act", ownerId), ("@t", stamp));
            if (fromRegister)
                Sql.Exec(c, "INSERT INTO cash_movements(shift_id,kind,amount_cents,source_type,source_id,created_at) VALUES(@s,'expense',@a,'expense',@e,@t)",
                    ("@s", shiftId), ("@a", -cents), ("@e", id), ("@t", stamp));
            Common.Audit(c, clock, ownerId, null, "expense.record", id.ToString(), category);
            return id;
        });
    }

    /// <summary>
    /// Blind close: the cashier supplies the physical count; expected cash is only returned afterwards.
    /// A nonzero variance needs an explanation. Nothing is rewritten to force a balance.
    /// </summary>
    public ShiftCloseResult CloseShift(long actorId, long shiftId, decimal countedCash, string? varianceReason)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            var role = Common.RequireActive(c, actorId);
            var shift = Sql.Query(c, "SELECT cashier_id,status FROM shifts WHERE id=@s", x => (Cashier: x.GetInt64(0), Status: x.GetString(1)), ("@s", shiftId)).FirstOrDefault();
            if (shift.Status != "open") throw new PosException(PosError.ShiftNotOpen, "The shift is not open.");
            if (role != Role.Owner && shift.Cashier != actorId)
                throw new PosException(PosError.PermissionDenied, "Only the shift's cashier or the owner can close it.");

            var counted = Money.ToCents(countedCash);
            if (counted < 0) throw new PosException(PosError.PaymentMismatch, "Counted cash cannot be negative.");
            var expected = Common.ExpectedCashCents(c, shiftId);
            var variance = counted - expected;
            if (variance != 0 && string.IsNullOrWhiteSpace(varianceReason))
                throw new PosException(PosError.InvalidDiscount, "A reason is required for a cash variance.");

            Sql.Exec(c,
                "UPDATE shifts SET status='closed',closed_at=@t,closing_count_cents=@c,expected_cash_cents=@e,variance_cents=@v,variance_reason=@r WHERE id=@s",
                ("@t", Common.Stamp(clock)), ("@c", counted), ("@e", expected), ("@v", variance), ("@r", varianceReason), ("@s", shiftId));
            Common.Audit(c, clock, actorId, null, "shift.close", shiftId.ToString(), varianceReason, $"variance={Money.FromCents(variance)}");
            return new ShiftCloseResult(Money.FromCents(expected), Money.FromCents(counted), Money.FromCents(variance));
        });
    }
}

public sealed record CountItem(long ProductId, decimal CountedQuantity, decimal? GainUnitCost = null);

public sealed class StockAdjustmentService(PosDatabase db, IClock clock)
{
    /// <summary>Posts one adjustment document from a stock count. Losses use average cost; gains need an explicit cost.</summary>
    public long ApplyCount(long ownerId, string reason, IReadOnlyList<CountItem> items)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            foreach(var item in items)StockTracking.RequireNoOpenCount(c,item.ProductId);
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM stock_locations WHERE active=1") > 1 || items.Any(i => StockTracking.Tracked(c, i.ProductId)
                || Sql.Scalar<long>(c,"SELECT COUNT(*) FROM stock_movements WHERE product_id=@p AND lot_id IS NOT NULL",("@p",i.ProductId))>0))
                throw new PosException(PosError.PermissionDenied, "Use a location-aware stock count or lot adjustment for this shop.");
            if (string.IsNullOrWhiteSpace(reason)) throw new PosException(PosError.InvalidDiscount, "A reason is required.");
            var stamp = Common.Stamp(clock);
            var docId = Sql.Insert(c, "INSERT INTO stock_adjustments(reason,actor_id,created_at) VALUES(@r,@a,@t)", ("@r", reason), ("@a", ownerId), ("@t", stamp));

            foreach (var item in items)
            {
                if (item.CountedQuantity < 0) throw new PosException(PosError.InvalidQuantity, "Counted quantity cannot be negative.");
                var onHand = Common.OnHand(c, item.ProductId);
                var delta = item.CountedQuantity - onHand;
                if (delta == 0) continue;
                var avg = Money.FromCost4(Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", item.ProductId)));

                if (delta < 0)
                {
                    var lossCents = Money.ToCents(-delta * avg);
                    Sql.Exec(c, "INSERT INTO stock_movements(product_id,qty_milli,unit_cost_x10000,source_type,source_id,reason,actor_id,created_at) VALUES(@p,@q,@u,'adjustment',@d,@r,@a,@t)",
                        ("@p", item.ProductId), ("@q", Money.ToMilli(delta)), ("@u", Money.ToCost4(avg)), ("@d", docId), ("@r", reason), ("@a", ownerId), ("@t", stamp));
                    Sql.Exec(c, "INSERT INTO stock_losses(product_id,qty_milli,cost_cents,source_type,source_id,reason,created_at) VALUES(@p,@q,@c,'adjustment',@d,@r,@t)",
                        ("@p", item.ProductId), ("@q", Money.ToMilli(-delta)), ("@c", lossCents), ("@d", docId), ("@r", reason), ("@t", stamp));
                }
                else
                {
                    var cost = item.GainUnitCost ?? throw new PosException(PosError.InvalidPrice, "A cost basis is required for a positive adjustment.");
                    var newAvg = CostCalculator.WeightedAverage(onHand, avg, delta, cost);
                    Sql.Exec(c, "INSERT INTO stock_movements(product_id,qty_milli,unit_cost_x10000,source_type,source_id,reason,actor_id,created_at) VALUES(@p,@q,@u,'adjustment',@d,@r,@a,@t)",
                        ("@p", item.ProductId), ("@q", Money.ToMilli(delta)), ("@u", Money.ToCost4(cost)), ("@d", docId), ("@r", reason), ("@a", ownerId), ("@t", stamp));
                    Sql.Exec(c, "UPDATE products SET avg_cost_x10000=@v WHERE id=@p", ("@v", Money.ToCost4(newAvg)), ("@p", item.ProductId));
                }
            }

            Common.Audit(c, clock, ownerId, null, "stock.adjust", docId.ToString(), reason);
            return docId;
        });
    }
}

// ---------------- reports ----------------

public sealed record SalesSummary(
    int SaleCount, decimal GrossSales, decimal Discounts, decimal ReturnsTotal, decimal NetSalesExTax, decimal Tax,
    IReadOnlyDictionary<string, decimal> PaymentsByMethod, decimal CostOfGoods, decimal GrossProfit, decimal StockLosses);

public sealed record ShiftReport(
    long ShiftId, string BusinessDate, decimal OpeningFloat, IReadOnlyDictionary<string, decimal> Movements,
    decimal Expected, decimal? Counted, decimal? Variance, string? VarianceReason);

public sealed record CustomerBalanceRow(long CustomerId, string Name, decimal Balance);

public sealed record LowStockRow(long ProductId, string Name, decimal OnHand, decimal Threshold);

public sealed partial class ReportService(PosDatabase db)
{
    // Business-date mode groups by the shift's date; timestamp mode groups by the local calendar date of posting.
    static string SaleDate(bool byBusinessDate) => byBusinessDate ? "s.business_date" : "substr(s.created_at,1,10)";
    static string ReturnDate(bool byBusinessDate) => byBusinessDate ? "(SELECT business_date FROM shifts WHERE id=r.shift_id)" : "substr(r.created_at,1,10)";

    public SalesSummary Summary(string fromDate, string toDate, bool byBusinessDate = true)
    {
        using var c = db.Open();
        var sd = SaleDate(byBusinessDate);
        var rd = ReturnDate(byBusinessDate);
        var range = new (string, object?)[] { ("@f", fromDate), ("@t", toDate) };

        var s = Sql.Query(c,
            $"SELECT COUNT(*),COALESCE(SUM(gross_cents),0),COALESCE(SUM(discount_cents),0),COALESCE(SUM(taxable_cents),0),COALESCE(SUM(tax_cents),0) FROM sales s WHERE s.status='posted' AND {sd} BETWEEN @f AND @t",
            x => (N: x.GetInt32(0), Gross: x.GetInt64(1), Disc: x.GetInt64(2), Taxable: x.GetInt64(3), Tax: x.GetInt64(4)), range)[0];
        var r = Sql.Query(c,
            $"SELECT COALESCE(SUM(total_cents),0),COALESCE(SUM(taxable_cents),0),COALESCE(SUM(tax_cents),0) FROM returns r WHERE {rd} BETWEEN @f AND @t",
            x => (Total: x.GetInt64(0), Taxable: x.GetInt64(1), Tax: x.GetInt64(2)), range)[0];
        var pay = Sql.Query(c,
            $"SELECT COALESCE(m.code,p.method),SUM(p.applied_cents) FROM payments p JOIN sales s ON s.id=p.sale_id LEFT JOIN payment_methods m ON m.id=p.method_id WHERE s.status='posted' AND {sd} BETWEEN @f AND @t GROUP BY COALESCE(m.code,p.method)",
            x => (x.GetString(0), Money.FromCents(x.GetInt64(1))), range).ToDictionary(x => x.Item1, x => x.Item2);
        var cogs = Sql.Scalar<long>(c,
            $"SELECT COALESCE(SUM(l.cogs_cents),0) FROM sale_lines l JOIN sales s ON s.id=l.sale_id WHERE s.status='posted' AND {sd} BETWEEN @f AND @t", range);
        var cogsBack = Sql.Scalar<long>(c,
            $"SELECT COALESCE(SUM(rl.cogs_reversed_cents),0) FROM return_lines rl JOIN returns r ON r.id=rl.return_id WHERE {rd} BETWEEN @f AND @t", range);
        var losses = Sql.Scalar<long>(c,
            "SELECT COALESCE(SUM(cost_cents),0) FROM stock_losses WHERE substr(created_at,1,10) BETWEEN @f AND @t", range);

        var netSales = s.Taxable - r.Taxable;
        var netCogs = cogs - cogsBack;
        return new SalesSummary(s.N, Money.FromCents(s.Gross), Money.FromCents(s.Disc), Money.FromCents(r.Total),
            Money.FromCents(netSales), Money.FromCents(s.Tax - r.Tax), pay, Money.FromCents(netCogs),
            Money.FromCents(netSales - netCogs), Money.FromCents(losses));
    }

    public ShiftReport Shift(long shiftId)
    {
        using var c = db.Open();
        var sh = Sql.Query(c,
            "SELECT business_date,opening_float_cents,closing_count_cents,variance_cents,variance_reason FROM shifts WHERE id=@s",
            x => (Date: x.GetString(0), Open: x.GetInt64(1), Count: x.IsDBNull(2) ? (long?)null : x.GetInt64(2),
                  Var: x.IsDBNull(3) ? (long?)null : x.GetInt64(3), Reason: x.IsDBNull(4) ? null : x.GetString(4)), ("@s", shiftId)).FirstOrDefault();
        if (sh.Date is null) throw new PosException(PosError.NotFound, "Shift not found.");
        var moves = Sql.Query(c, "SELECT kind,SUM(amount_cents) FROM cash_movements WHERE shift_id=@s GROUP BY kind",
            x => (x.GetString(0), Money.FromCents(x.GetInt64(1))), ("@s", shiftId)).ToDictionary(x => x.Item1, x => x.Item2);
        return new ShiftReport(shiftId, sh.Date, Money.FromCents(sh.Open), moves, Money.FromCents(Common.ExpectedCashCents(c, shiftId)),
            sh.Count is long k ? Money.FromCents(k) : null, sh.Var is long v ? Money.FromCents(v) : null, sh.Reason);
    }

    public IReadOnlyList<CustomerBalanceRow> CustomerBalances()
    {
        using var c = db.Open();
        return Sql.Query(c,
            "SELECT c.id,c.name,COALESCE(SUM(l.amount_cents),0) b FROM customers c LEFT JOIN customer_ledger l ON l.customer_id=c.id GROUP BY c.id HAVING b<>0 ORDER BY b DESC",
            x => new CustomerBalanceRow(x.GetInt64(0), x.GetString(1), Money.FromCents(x.GetInt64(2))));
    }

    public IReadOnlyList<LowStockRow> LowStock()
    {
        using var c = db.Open();
        return Sql.Query(c,
            """
            SELECT p.id,p.name,COALESCE(SUM(m.qty_milli),0) q,p.low_stock_milli FROM products p
            LEFT JOIN stock_movements m ON m.product_id=p.id WHERE p.active=1 GROUP BY p.id HAVING q<=p.low_stock_milli ORDER BY q
            """,
            x => new LowStockRow(x.GetInt64(0), x.GetString(1), Money.FromMilli(x.GetInt64(2)), Money.FromMilli(x.GetInt64(3))));
    }

    public decimal ExpensesTotal(string fromDate, string toDate)
    {
        using var c = db.Open();
        return Money.FromCents(Sql.Scalar<long>(c, "SELECT COALESCE(SUM(amount_cents),0) FROM expenses WHERE expense_date BETWEEN @f AND @t", ("@f", fromDate), ("@t", toDate)));
    }
}
