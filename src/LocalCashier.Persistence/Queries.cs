using System.Globalization;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record InvoiceRow(long Id, string Number, string CreatedAt, string BusinessDate, string? Customer, string Cashier, int Units,
    string PaymentKey, decimal Total, string StatusKey,string? PaymentNameEn=null,string? PaymentNameAr=null);

public sealed record DashboardStats(decimal Sales, int Invoices, int Units, decimal GrossProfit);

public sealed record DailyPoint(string Date, decimal Sales);

public sealed record CategoryTotal(string CategoryKey, decimal Sales, double Percent);

public sealed record TopSeller(string NameEn, string? NameAlt, int Units, decimal Sales);

public sealed record MovementRow(long Id, string CreatedAt, string TypeKey, string NameEn, string? NameAlt, string? Size, string? ColorEn, string? ColorAlt, int Qty, string Reference);

public sealed record ExpenseRow(long Id, string Date, string Category, string? Description, decimal Amount, bool FromRegister, string RecordedBy);

public sealed record ShiftInfo(long Id, long CashierId, string CashierName, string OpenedAt, string BusinessDate, decimal OpeningFloat);

public sealed record ShiftHistoryRow(long Id, string BusinessDate, string Cashier, string OpenedAt, string? ClosedAt, decimal OpeningFloat, decimal? Counted, decimal? Expected, decimal? Variance, string? VarianceReason, bool IsOpen);

public sealed record CashMovementRow(string CreatedAt, string Kind, decimal Amount);

public sealed record DiagnosticsInfo(string DatabasePath, long DatabaseBytes, int SchemaVersion, string AppVersion, long FreeDiskBytes, string DriveName);

/// <summary>Read models for the screens: invoices, dashboard figures, stock movements, expenses, shifts and diagnostics.</summary>
public sealed class QueryService(PosDatabase db, IClock clock)
{
    // ---------------------------------------------------------------- invoices
    /// <summary>Posted sales in a business-date range with derived payment and status. Filters are applied by the caller's keys.</summary>
    public IReadOnlyList<InvoiceRow> Invoices(string fromDate, string toDate, string? text = null, string? statusKey = null, string? paymentKey = null, int limit = 2000, long? customerId = null, long? cashierId = null)
    {
        using var c = db.Open();
        var rows = Sql.Query(c,
            """
            SELECT s.id,s.number,s.created_at,s.business_date,c.name,u.display_name,
              (SELECT COALESCE(SUM(qty_milli),0) FROM sale_lines WHERE sale_id=s.id),
              s.total_cents,
              (SELECT COALESCE(SUM(total_cents),0) FROM returns WHERE sale_id=s.id),
              (SELECT COUNT(*) FROM payments WHERE sale_id=s.id),
              (SELECT method FROM payments WHERE sale_id=s.id ORDER BY id LIMIT 1),
              (SELECT method_name_en FROM payments WHERE sale_id=s.id ORDER BY id LIMIT 1),
              (SELECT method_name_ar FROM payments WHERE sale_id=s.id ORDER BY id LIMIT 1)
            FROM sales s LEFT JOIN customers c ON c.id=s.customer_id JOIN users u ON u.id=s.cashier_id
            WHERE s.status='posted' AND s.business_date BETWEEN @f AND @t AND (@cu IS NULL OR s.customer_id=@cu) AND (@ca IS NULL OR s.cashier_id=@ca)
            ORDER BY s.id DESC LIMIT @l
            """,
            x => (Id: x.GetInt64(0), Number: x.GetString(1), At: x.GetString(2), Date: x.GetString(3), Cust: x.IsDBNull(4) ? null : x.GetString(4), Cashier: x.GetString(5),
                  Units: (int)(x.GetInt64(6) / 1000), Total: x.GetInt64(7), Returned: x.GetInt64(8), PayCount: x.GetInt64(9), Method: x.IsDBNull(10) ? null : x.GetString(10),NameEn:x.IsDBNull(11)?null:x.GetString(11),NameAr:x.IsDBNull(12)?null:x.GetString(12)),
            ("@f", fromDate), ("@t", toDate), ("@l", limit), ("@cu", customerId), ("@ca", cashierId));

        var list = new List<InvoiceRow>();
        foreach (var r in rows)
        {
            var status = r.Returned >= r.Total && r.Total > 0 ? "Returned" : Common.SaleOutstandingCents(c, r.Id) > 0 ? "Partial" : "Paid";
            var pay = r.PayCount == 0 ? "Credit" : r.PayCount > 1 ? "Split" : char.ToUpperInvariant(r.Method![0]) + r.Method[1..];
            list.Add(new InvoiceRow(r.Id, r.Number, r.At, r.Date, r.Cust, r.Cashier, r.Units, pay, Money.FromCents(r.Total), status,r.PayCount==1?r.NameEn:null,r.PayCount==1?r.NameAr:null));
        }

        IEnumerable<InvoiceRow> q = list;
        if (!string.IsNullOrWhiteSpace(text))
            q = q.Where(i => i.Number.Contains(text, StringComparison.OrdinalIgnoreCase) || (i.Customer?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false));
        if (!string.IsNullOrWhiteSpace(statusKey)) q = q.Where(i => i.StatusKey == statusKey);
        if (!string.IsNullOrWhiteSpace(paymentKey)) q = q.Where(i => i.PaymentKey == paymentKey);
        return q.ToList();
    }

    // ---------------------------------------------------------------- dashboard
    /// <summary>Net figures for a business-date range (returns subtracted). <paramref name="cashierId"/> narrows sales and returns to one user.</summary>
    public DashboardStats Stats(string fromDate, string toDate, long? cashierId = null)
    {
        using var c = db.Open();
        var args = new (string, object?)[] { ("@f", fromDate), ("@t", toDate), ("@ca", cashierId) };
        const string saleWhere = "s.status='posted' AND s.business_date BETWEEN @f AND @t AND (@ca IS NULL OR s.cashier_id=@ca)";
        const string retWhere = "sh.business_date BETWEEN @f AND @t AND (@ca IS NULL OR r.actor_id=@ca)";
        var sales = Sql.Scalar<long>(c, $"SELECT COALESCE(SUM(s.total_cents),0) FROM sales s WHERE {saleWhere}", args);
        var count = (int)Sql.Scalar<long>(c, $"SELECT COUNT(*) FROM sales s WHERE {saleWhere}", args);
        var retTotal = Sql.Scalar<long>(c, $"SELECT COALESCE(SUM(r.total_cents),0) FROM returns r JOIN shifts sh ON sh.id=r.shift_id WHERE {retWhere}", args);
        var lines = Sql.Query(c,
            $"SELECT COALESCE(SUM(l.qty_milli),0),COALESCE(SUM(l.taxable_cents),0),COALESCE(SUM(l.cogs_cents),0) FROM sale_lines l JOIN sales s ON s.id=l.sale_id WHERE {saleWhere}",
            x => (Qty: x.GetInt64(0), Taxable: x.GetInt64(1), Cogs: x.GetInt64(2)), args)[0];
        var back = Sql.Query(c,
            $"SELECT COALESCE(SUM(rl.qty_milli),0),COALESCE(SUM(rl.taxable_cents),0),COALESCE(SUM(rl.cogs_reversed_cents),0) FROM return_lines rl JOIN returns r ON r.id=rl.return_id JOIN shifts sh ON sh.id=r.shift_id WHERE {retWhere}",
            x => (Qty: x.GetInt64(0), Taxable: x.GetInt64(1), Cogs: x.GetInt64(2)), args)[0];
        var profit = (lines.Taxable - back.Taxable) - (lines.Cogs - back.Cogs);
        return new DashboardStats(Money.FromCents(sales - retTotal), count, (int)((lines.Qty - back.Qty) / 1000), Money.FromCents(profit));
    }

    public IReadOnlyList<DailyPoint> Daily(string fromDate, string toDate, long? cashierId = null)
    {
        using var c = db.Open();
        var sold = Sql.Query(c, "SELECT business_date,SUM(total_cents) FROM sales WHERE status='posted' AND business_date BETWEEN @f AND @t AND (@ca IS NULL OR cashier_id=@ca) GROUP BY business_date",
            x => (x.GetString(0), x.GetInt64(1)), ("@f", fromDate), ("@t", toDate), ("@ca", cashierId)).ToDictionary(x => x.Item1, x => x.Item2);
        var back = Sql.Query(c,
            "SELECT sh.business_date,SUM(r.total_cents) FROM returns r JOIN shifts sh ON sh.id=r.shift_id WHERE sh.business_date BETWEEN @f AND @t AND (@ca IS NULL OR r.actor_id=@ca) GROUP BY sh.business_date",
            x => (x.GetString(0), x.GetInt64(1)), ("@f", fromDate), ("@t", toDate), ("@ca", cashierId)).ToDictionary(x => x.Item1, x => x.Item2);
        var from = DateTime.ParseExact(fromDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = DateTime.ParseExact(toDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var list = new List<DailyPoint>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var key = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            list.Add(new DailyPoint(key, Money.FromCents(sold.GetValueOrDefault(key) - back.GetValueOrDefault(key))));
        }

        return list;
    }

    public IReadOnlyList<CategoryTotal> Categories(string fromDate, string toDate, long? cashierId = null)
    {
        using var c = db.Open();
        var rows = Sql.Query(c,
            """
            SELECT COALESCE(NULLIF(p.category,''),'Others'),SUM(l.total_cents) FROM sale_lines l JOIN sales s ON s.id=l.sale_id JOIN products p ON p.id=l.product_id
            WHERE s.status='posted' AND s.business_date BETWEEN @f AND @t AND (@ca IS NULL OR s.cashier_id=@ca) GROUP BY 1 ORDER BY 2 DESC
            """,
            x => (Key: x.GetString(0), Cents: x.GetInt64(1)), ("@f", fromDate), ("@t", toDate), ("@ca", cashierId));
        var total = (double)rows.Sum(r => r.Cents);
        return rows.Select(r => new CategoryTotal(r.Key, Money.FromCents(r.Cents), total == 0 ? 0 : r.Cents * 100d / total)).ToList();
    }

    public IReadOnlyList<TopSeller> TopSellers(string fromDate, string toDate, int count = 5, long? cashierId = null)
    {
        using var c = db.Open();
        return Sql.Query(c,
            """
            SELECT st.name_en,st.name_alt,SUM(l.qty_milli),SUM(l.total_cents) FROM sale_lines l JOIN sales s ON s.id=l.sale_id JOIN products p ON p.id=l.product_id
            JOIN styles st ON st.id=p.style_id WHERE s.status='posted' AND s.business_date BETWEEN @f AND @t AND (@ca IS NULL OR s.cashier_id=@ca) GROUP BY st.id ORDER BY SUM(l.total_cents) DESC LIMIT @n
            """,
            x => new TopSeller(x.GetString(0), x.IsDBNull(1) ? null : x.GetString(1), (int)(x.GetInt64(2) / 1000), Money.FromCents(x.GetInt64(3))),
            ("@f", fromDate), ("@t", toDate), ("@n", count), ("@ca", cashierId));
    }

    // ---------------------------------------------------------------- stock movements
    /// <summary>Stock ledger rows. typeKey is Sale, Purchase, Adjustment or Return; dates are local calendar dates of posting.</summary>
    public IReadOnlyList<MovementRow> Movements(string fromDate, string toDate, string? typeKey = null, int limit = 1000)
    {
        using var c = db.Open();
        var rows = Sql.Query(c,
            """
            SELECT m.id,m.created_at,m.source_type,p.name,p.name_alt,p.size,p.color_name_en,p.color_name_alt,m.qty_milli,
              CASE m.source_type
                WHEN 'sale' THEN (SELECT number FROM sales WHERE id=m.source_id)
                WHEN 'return' THEN (SELECT number FROM returns WHERE id=m.source_id)
                WHEN 'receipt' THEN COALESCE(NULLIF((SELECT reference FROM stock_receipts WHERE id=m.source_id),''),'PO-'||printf('%05d',m.source_id))
                WHEN 'adjustment' THEN 'ADJ-'||printf('%05d',m.source_id)
                ELSE m.source_type END
            FROM stock_movements m JOIN products p ON p.id=m.product_id
            WHERE substr(m.created_at,1,10) BETWEEN @f AND @t ORDER BY m.id DESC LIMIT @l
            """,
            x => new MovementRow(x.GetInt64(0), x.GetString(1), TypeOf(x.GetString(2)), x.GetString(3), x.IsDBNull(4) ? null : x.GetString(4),
                x.IsDBNull(5) ? null : x.GetString(5), x.IsDBNull(6) ? null : x.GetString(6), x.IsDBNull(7) ? null : x.GetString(7),
                (int)(x.GetInt64(8) / 1000), x.IsDBNull(9) ? "" : x.GetString(9)),
            ("@f", fromDate), ("@t", toDate), ("@l", limit));
        return string.IsNullOrWhiteSpace(typeKey) ? rows : rows.Where(r => r.TypeKey == typeKey).ToList();
    }

    static string TypeOf(string source) => source switch { "sale" => "Sale", "receipt" => "Purchase", "return" => "Return", _ => "Adjustment" };

    // ---------------------------------------------------------------- expenses
    public IReadOnlyList<ExpenseRow> Expenses(string fromDate, string toDate)
    {
        using var c = db.Open();
        return Sql.Query(c,
            "SELECT e.id,e.expense_date,e.category,e.description,e.amount_cents,e.source,u.display_name FROM expenses e JOIN users u ON u.id=e.actor_id WHERE e.expense_date BETWEEN @f AND @t ORDER BY e.expense_date DESC, e.id DESC",
            x => new ExpenseRow(x.GetInt64(0), x.GetString(1), x.GetString(2), x.IsDBNull(3) ? null : x.GetString(3), Money.FromCents(x.GetInt64(4)), x.GetString(5) == "register", x.GetString(6)),
            ("@f", fromDate), ("@t", toDate));
    }

    // ---------------------------------------------------------------- shifts
    public ShiftInfo? CurrentShift()
    {
        using var c = db.Open();
        return Sql.Query(c,
            "SELECT s.id,s.cashier_id,u.display_name,s.opened_at,s.business_date,s.opening_float_cents FROM shifts s JOIN users u ON u.id=s.cashier_id WHERE s.status='open'",
            x => new ShiftInfo(x.GetInt64(0), x.GetInt64(1), x.GetString(2), x.GetString(3), x.GetString(4), Money.FromCents(x.GetInt64(5)))).FirstOrDefault();
    }

    public IReadOnlyList<ShiftHistoryRow> ShiftHistory(int limit = 60,long? cashierId=null,string? fromDate=null,string? toDate=null)
    {
        using var c = db.Open();
        return Sql.Query(c,
            """
            SELECT s.id,s.business_date,u.display_name,s.opened_at,s.closed_at,s.opening_float_cents,s.closing_count_cents,s.expected_cash_cents,s.variance_cents,s.variance_reason,s.status
            FROM shifts s JOIN users u ON u.id=s.cashier_id
            WHERE (@ca IS NULL OR s.cashier_id=@ca) AND (@f IS NULL OR s.business_date>=@f) AND (@t IS NULL OR s.business_date<=@t) ORDER BY s.id DESC LIMIT @l
            """,
            x => new ShiftHistoryRow(x.GetInt64(0), x.GetString(1), x.GetString(2), x.GetString(3), x.IsDBNull(4) ? null : x.GetString(4), Money.FromCents(x.GetInt64(5)),
                x.IsDBNull(6) ? null : Money.FromCents(x.GetInt64(6)), x.IsDBNull(7) ? null : Money.FromCents(x.GetInt64(7)),
                x.IsDBNull(8) ? null : Money.FromCents(x.GetInt64(8)), x.IsDBNull(9) ? null : x.GetString(9), x.GetString(10) == "open"),
            ("@l", limit),("@ca",cashierId),("@f",fromDate),("@t",toDate));
    }

    public IReadOnlyList<CashMovementRow> CashMovements(long shiftId)
    {
        using var c = db.Open();
        return Sql.Query(c, "SELECT created_at,kind,amount_cents FROM cash_movements WHERE shift_id=@s ORDER BY id",
            x => new CashMovementRow(x.GetString(0), x.GetString(1), Money.FromCents(x.GetInt64(2))), ("@s", shiftId));
    }

    // ---------------------------------------------------------------- stock levels and customer payments
    /// <summary>Every active variant with its ledger quantity, for the stock-level and stock-count screens.</summary>
    public IReadOnlyList<(VariantRow Variant, string StyleNameEn, string? StyleNameAlt, string Kind, int Alert)> StockLevels()
    {
        using var c = db.Open();
        return Sql.Query(c,
            """
            SELECT p.id,p.style_id,p.sku,p.barcode,p.size,p.color_name_en,p.color_name_alt,p.color_hex,p.price_cents,p.avg_cost_x10000,p.active,
              COALESCE((SELECT SUM(qty_milli) FROM stock_movements WHERE product_id=p.id),0),st.name_en,st.name_alt,st.kind,st.low_stock_milli,p.unit,p.allow_decimal
            FROM products p JOIN styles st ON st.id=p.style_id WHERE p.active=1 ORDER BY st.name_en,p.id
            """,
            x => (new VariantRow(x.GetInt64(0), x.GetInt64(1), x.GetString(2), x.IsDBNull(3) ? null : x.GetString(3), x.IsDBNull(4) ? null : x.GetString(4),
                    x.IsDBNull(5) ? null : x.GetString(5), x.IsDBNull(6) ? null : x.GetString(6), x.IsDBNull(7) ? null : x.GetString(7),
                    Money.FromCents(x.GetInt64(8)), Money.FromCost4(x.GetInt64(9)), x.GetInt64(11) / 1000m, x.GetInt64(10) == 1, x.GetString(16), x.GetInt64(17) == 1),
                x.GetString(12), x.IsDBNull(13) ? null : x.GetString(13), x.GetString(14), (int)(x.GetInt64(15) / 1000)));
    }

    public sealed record CustomerPaymentRow(string CreatedAt, string Number, string Method, decimal Amount);

    public IReadOnlyList<CustomerPaymentRow> CustomerPayments(long customerId)
    {
        using var c = db.Open();
        return Sql.Query(c, "SELECT created_at,number,method,amount_cents FROM collections WHERE customer_id=@c ORDER BY id DESC",
            x => new CustomerPaymentRow(x.GetString(0), x.GetString(1), x.GetString(2), Money.FromCents(x.GetInt64(3))), ("@c", customerId));
    }

    // ---------------------------------------------------------------- diagnostics
    public DiagnosticsInfo Diagnostics()
    {
        var path = System.IO.Path.GetFullPath(db.Path);
        var size = System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : 0;
        var root = System.IO.Path.GetPathRoot(path) ?? "";
        long free = 0;
        try { free = new System.IO.DriveInfo(root).AvailableFreeSpace; } catch { /* network or unavailable drive */ }
        return new DiagnosticsInfo(path, size, PosDatabase.SchemaVersion, BackupService.AppVersion, free, root);
    }

    /// <summary>Business date "today" using the injected clock (so tests and the app agree).</summary>
    public string Today => clock.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
