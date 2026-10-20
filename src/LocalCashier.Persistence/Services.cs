using LocalCashier.Application;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

internal static class Common
{
    public static string Stamp(IClock clock) => clock.Now.ToString("o");

    public static void Audit(SqliteConnection c, IClock clock, long? actor, long? approver, string action, string? target, string? reason = null, string? details = null) =>
        Sql.Exec(c,
            "INSERT INTO audit_events(ts,actor_id,approver_id,action,target,reason,details) VALUES(@ts,@a,@ap,@act,@t,@r,@d)",
            ("@ts", Stamp(clock)), ("@a", actor), ("@ap", approver), ("@act", action), ("@t", target), ("@r", reason), ("@d", details));

    /// <summary>Returns the role of an active user or throws.</summary>
    public static Role RequireActive(SqliteConnection c, long userId)
    {
        var rows = Sql.Query(c, "SELECT role, active FROM users WHERE id=@id",
            r => (Role: r.GetString(0), Active: r.GetInt64(1) == 1), ("@id", userId));
        if (rows.Count == 0 || !rows[0].Active)
            throw new PosException(PosError.PermissionDenied, "Unknown or inactive user.");
        return rows[0].Role == "owner" ? Role.Owner : Role.Cashier;
    }

    public static void RequireOwner(SqliteConnection c, long userId)
    {
        if (RequireActive(c, userId) != Role.Owner)
            throw new PosException(PosError.PermissionDenied, "This operation requires the owner.");
    }

    /// <summary>Next document number. A restore bumps the epoch so numbers never repeat those lost after the backup cutoff.</summary>
    public static string NextNumber(SqliteConnection c, string prefix)
    {
        var epoch = Sql.Scalar<long?>(c, "SELECT value FROM settings WHERE key='number_epoch'") ?? 1;
        var series = $"{prefix}{epoch}";
        var seq = Sql.Scalar<long?>(c, "SELECT next_value FROM document_sequences WHERE series=@s", ("@s", series));
        if (seq is null) Sql.Exec(c, "INSERT INTO document_sequences(series,next_value) VALUES(@s,2)", ("@s", series));
        else Sql.Exec(c, "UPDATE document_sequences SET next_value=next_value+1 WHERE series=@s", ("@s", series));
        return $"{series}-{(seq ?? 1):D6}";
    }

    public static void RequireOpenShift(SqliteConnection c, long shiftId)
    {
        if (Sql.Scalar<string>(c, "SELECT status FROM shifts WHERE id=@s", ("@s", shiftId)) != "open")
            throw new PosException(PosError.ShiftNotOpen, "The shift is not open.");
    }

    public static long ExpectedCashCents(SqliteConnection c, long shiftId) =>
        Sql.Scalar<long>(c, "SELECT opening_float_cents FROM shifts WHERE id=@s", ("@s", shiftId))
        + Sql.Scalar<long>(c, "SELECT COALESCE(SUM(amount_cents),0) FROM cash_movements WHERE shift_id=@s", ("@s", shiftId));

    /// <summary>Debt still owed on one credit invoice: original debt less collections and return reductions.</summary>
    public static long SaleOutstandingCents(SqliteConnection c, long saleId) =>
        Sql.Scalar<long>(c, "SELECT debt_cents FROM sales WHERE id=@s", ("@s", saleId))
        - Sql.Scalar<long>(c, "SELECT COALESCE(SUM(a.amount_cents),0) FROM collection_allocations a WHERE a.sale_id=@s AND a.collection_id NOT IN(SELECT collection_id FROM collection_reversals)", ("@s", saleId))
        - Sql.Scalar<long>(c, "SELECT COALESCE(SUM(debt_reduction_cents),0) FROM returns WHERE sale_id=@s", ("@s", saleId));

    /// <summary>Local business date (yyyy-MM-dd) from the injected clock; the same source for every due/overdue test.</summary>
    public static string Today(IClock clock) => clock.Now.ToString("yyyy-MM-dd");

    public static decimal OnHand(SqliteConnection c, long productId) =>
        Money.FromMilli(Sql.Scalar<long>(c, "SELECT COALESCE(SUM(qty_milli),0) FROM stock_movements WHERE product_id=@p", ("@p", productId)));

    public static long CustomerBalanceCents(SqliteConnection c, long customerId) =>
        Sql.Scalar<long>(c, "SELECT COALESCE(SUM(amount_cents),0) FROM customer_ledger WHERE customer_id=@c", ("@c", customerId));
}

public sealed partial class UserService(PosDatabase db, IClock clock)
{
    public long Create(string username, string displayName, Role role, string password)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            var id = Sql.Insert(c,
                "INSERT INTO users(username,display_name,role,password_hash) VALUES(@u,@d,@r,@h)",
                ("@u", username), ("@d", displayName), ("@r", role == Role.Owner ? "owner" : "cashier"), ("@h", PasswordHasher.Hash(password)));
            Common.Audit(c, clock, null, null, "user.create", username);
            return id;
        });
    }

    public bool AnyUsers()
    {
        using var c = db.Open();
        return Sql.Scalar<long>(c, "SELECT COUNT(*) FROM users") > 0;
    }

    public UserInfo? Authenticate(string username, string password)
    {
        using var c = db.Open();
        var rows = Sql.Query(c, "SELECT id, username, display_name, role, password_hash FROM users WHERE username=@u AND active=1",
            r => (Info: new UserInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3) == "owner" ? Role.Owner : Role.Cashier), Hash: r.GetString(4)),
            ("@u", username));
        return rows.Count == 1 && PasswordHasher.Verify(password, rows[0].Hash) ? rows[0].Info : null;
    }
}

public sealed class SettingsService(PosDatabase db, IClock clock)
{
    static readonly HashSet<string> OwnerKeys = ["backup_external_dir", "store_name", "store_phone", "store_address", "receipt_footer", "receipt_language", "printer_receipt", "printer_a4", "printer_label", "label_size", "auto_print_receipt", "default_tax_percent", "lock_minutes", "start_fullscreen"];

    public string? GetValue(string key)
    {
        using var c = db.Open();
        return Sql.Scalar<string>(c, "SELECT value FROM settings WHERE key=@k", ("@k", key));
    }

    /// <summary>Owner-only free-form settings (an allow-list of keys).</summary>
    public void SetValue(long ownerId, string key, string value)
    {
        if (!OwnerKeys.Contains(key)) throw new PosException(PosError.PermissionDenied, "Unknown setting.");
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            Sql.Exec(c, "INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=@v", ("@k", key), ("@v", value));
            Common.Audit(c, clock, ownerId, null, "settings.set", key);
            return 0;
        });
    }

    /// <summary>Null until the owner has explicitly configured tax.</summary>
    public bool? PricesIncludeTax()
    {
        using var c = db.Open();
        var v = Sql.Scalar<string>(c, "SELECT value FROM settings WHERE key='tax_price_mode'");
        return v is null ? null : v == "inclusive";
    }

    /// <summary>Tax must be explicitly configured by the owner before real sales can be posted.</summary>
    public void ConfigureTax(long ownerId, bool pricesIncludeTax)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            Sql.Exec(c, "INSERT INTO settings(key,value) VALUES('tax_price_mode',@v) ON CONFLICT(key) DO UPDATE SET value=@v",
                ("@v", pricesIncludeTax ? "inclusive" : "exclusive"));
            Common.Audit(c, clock, ownerId, null, "settings.tax", pricesIncludeTax ? "inclusive" : "exclusive");
            return 0;
        });
    }
}

public sealed record ProductInfo(long Id, string Sku, string? Barcode, string Name, string? NameAlt, decimal Price, decimal TaxRate, bool AllowDecimal, bool Active,
    string? Size = null, string? ColorNameEn = null, string? ColorNameAlt = null, string? ColorHex = null, long StyleId = 0);

public sealed class CatalogService(PosDatabase db, IClock clock)
{
    public long AddProduct(long ownerId, string sku, string? barcode, string name, string? nameAlt, decimal price,
        decimal taxRate = 0m, bool allowDecimal = false, string unit = "pc", string? category = null)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            var id = Sql.Insert(c,
                """
                INSERT INTO products(sku,barcode,name,name_alt,category,unit,price_cents,allow_decimal,tax_rate_bp)
                VALUES(@s,@b,@n,@na,@cat,@u,@p,@d,@t)
                """,
                ("@s", sku), ("@b", barcode), ("@n", name), ("@na", nameAlt), ("@cat", category), ("@u", unit),
                ("@p", Money.ToCents(price)), ("@d", allowDecimal ? 1 : 0), ("@t", decimal.ToInt64(taxRate * 10000m)));
            // A simple product is a one-variant style, so catalog screens and reports treat everything uniformly.
            var styleId = Sql.Insert(c,
                "INSERT INTO styles(name_en,name_alt,category,base_price_cents,tax_rate_bp,active) VALUES(@n,@na,@cat,@p,@t,1)",
                ("@n", name), ("@na", nameAlt), ("@cat", category), ("@p", Money.ToCents(price)), ("@t", decimal.ToInt64(taxRate * 10000m)));
            Sql.Exec(c, "UPDATE products SET style_id=@s WHERE id=@i", ("@s", styleId), ("@i", id));
            Common.Audit(c, clock, ownerId, null, "product.create", sku);
            return id;
        });
    }

    static ProductInfo Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        Money.FromCents(r.GetInt64(5)), r.GetInt64(6) / 10000m, r.GetInt64(7) == 1, r.GetInt64(8) == 1,
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? 0 : r.GetInt64(13));

    const string Select = "SELECT id,sku,barcode,name,name_alt,price_cents,tax_rate_bp,allow_decimal,active,size,color_name_en,color_name_alt,color_hex,style_id FROM products";

    public ProductInfo? GetById(long productId)
    {
        using var c = db.Open();
        return Sql.Query(c, Select + " WHERE id=@i", Map, ("@i", productId)).FirstOrDefault();
    }

    /// <summary>Exact, text-based barcode match (leading zeros preserved). Null when unknown; never auto-creates.</summary>
    public ProductInfo? FindByBarcode(string barcode)
    {
        using var c = db.Open();
        return Sql.Query(c, Select + " WHERE barcode=@b", Map, ("@b", barcode)).FirstOrDefault();
    }

    public IReadOnlyList<ProductInfo> Search(string text, int limit = 50)
    {
        using var c = db.Open();
        return Sql.Query(c, Select + " WHERE active=1 AND (name LIKE @q OR name_alt LIKE @q OR sku LIKE @q OR barcode=@e OR color_name_en LIKE @q OR color_name_alt LIKE @q OR size=@e) ORDER BY name,id LIMIT @l",
            Map, ("@q", "%" + text + "%"), ("@e", text), ("@l", limit));
    }

    public decimal OnHand(long productId)
    {
        using var c = db.Open();
        return Common.OnHand(c, productId);
    }

    /// <summary>Active products with their ledger-derived quantity, for stock counts.</summary>
    public IReadOnlyList<StockRow> ListStock()
    {
        using var c = db.Open();
        return Sql.Query(c,
            """
            SELECT p.id,p.sku,p.name,COALESCE(SUM(m.qty_milli),0),p.avg_cost_x10000 FROM products p
            LEFT JOIN stock_movements m ON m.product_id=p.id WHERE p.active=1 GROUP BY p.id ORDER BY p.name
            """,
            x => new StockRow(x.GetInt64(0), x.GetString(1), x.GetString(2), Money.FromMilli(x.GetInt64(3)), Money.FromCost4(x.GetInt64(4))));
    }
}

public sealed record CustomerRow(long Id, string Name, string? Phone, bool CreditEnabled, decimal CreditLimit, decimal Balance, decimal TotalPurchases = 0m, string? Email = null);

public sealed record CustomerTopItem(string Name, decimal Quantity, decimal Spent);

public sealed record CustomerStats(int Invoices, decimal Purchases, int Returns, decimal ReturnsTotal, decimal Net, decimal AverageBasket,
    string? FirstPurchase, string? LastPurchase, IReadOnlyList<CustomerTopItem> TopItems);

public sealed record StatementEntry(string At, string Type, decimal Amount, decimal Running, string? Document);

public sealed record OpenInvoice(long SaleId, string Number, string At, decimal Total, decimal Outstanding);

public sealed record StockRow(long ProductId, string Sku, string Name, decimal OnHand, decimal AvgCost);

public sealed class CustomerService(PosDatabase db, IClock clock)
{
    public long Add(long actorId, string name, string? phone = null, bool creditEnabled = false, decimal creditLimit = 0m, string? email = null)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            var role = Common.RequireActive(c, actorId);
            if (role != Role.Owner && (creditEnabled || creditLimit != 0m))
                throw new PosException(PosError.PermissionDenied, "Only the owner can enable credit.");
            var id = Sql.Insert(c,
                "INSERT INTO customers(name,phone,credit_enabled,credit_limit_cents,email,created_at) VALUES(@n,@p,@ce,@cl,@em,@ts)",
                ("@n", name), ("@p", phone), ("@ce", creditEnabled ? 1 : 0), ("@cl", Money.ToCents(creditLimit)), ("@em", email), ("@ts", Common.Stamp(clock)));
            Common.Audit(c, clock, actorId, null, "customer.create", name);
            return id;
        });
    }

    /// <summary>Checkout capture: reuses the active customer with this phone number, otherwise adds a new one. Any cashier may do this.</summary>
    public long FindOrAdd(long actorId, string name, string? phone)
    {
        phone = NormalizePhone(phone);
        if (phone is not null)
        {
            using var c = db.Open();
            var found = Sql.Scalar<long?>(c, "SELECT id FROM customers WHERE active=1 AND phone=@p ORDER BY id LIMIT 1", ("@p", phone));
            if (found is long id) return id;
        }
        if (string.IsNullOrWhiteSpace(name)) throw new PosException(PosError.InvalidPrice, "A customer name is required.");
        return Add(actorId, name.Trim(), phone);
    }

    public static string? NormalizePhone(string? phone)
    {
        var p = new string((phone ?? "").Where(ch => !char.IsWhiteSpace(ch) && ch != '-').ToArray());
        return p.Length == 0 ? null : p;
    }

    /// <summary>Lifetime figures for the customer page: purchases, returns, average basket, first/last visit and favourite items.</summary>
    public CustomerStats Stats(long customerId)
    {
        using var c = db.Open();
        var s = Sql.Query(c,
            "SELECT COUNT(*),COALESCE(SUM(total_cents),0),MIN(created_at),MAX(created_at) FROM sales WHERE customer_id=@c AND status='posted'",
            x => (N: x.GetInt64(0), Total: x.GetInt64(1), First: x.IsDBNull(2) ? null : x.GetString(2), Last: x.IsDBNull(3) ? null : x.GetString(3)), ("@c", customerId))[0];
        var r = Sql.Query(c,
            "SELECT COUNT(*),COALESCE(SUM(r.total_cents),0) FROM returns r JOIN sales s ON s.id=r.sale_id WHERE s.customer_id=@c",
            x => (N: x.GetInt64(0), Total: x.GetInt64(1)), ("@c", customerId))[0];
        var top = Sql.Query(c,
            """
            SELECT l.name,SUM(l.qty_milli),SUM(l.total_cents) FROM sale_lines l JOIN sales s ON s.id=l.sale_id
            WHERE s.customer_id=@c AND s.status='posted' GROUP BY l.name ORDER BY SUM(l.total_cents) DESC LIMIT 5
            """,
            x => new CustomerTopItem(x.GetString(0), Money.FromMilli(x.GetInt64(1)), Money.FromCents(x.GetInt64(2))), ("@c", customerId));
        var net = s.Total - r.Total;
        return new CustomerStats((int)s.N, Money.FromCents(s.Total), (int)r.N, Money.FromCents(r.Total), Money.FromCents(net),
            s.N == 0 ? 0m : Money.FromCents(s.Total / s.N), s.First, s.Last, top);
    }

    public IReadOnlyList<CustomerRow> List()
    {
        using var c = db.Open();
        return Sql.Query(c,
            """
            SELECT c.id,c.name,c.phone,c.credit_enabled,c.credit_limit_cents,
              (SELECT COALESCE(SUM(amount_cents),0) FROM customer_ledger WHERE customer_id=c.id),
              (SELECT COALESCE(SUM(total_cents),0) FROM sales WHERE customer_id=c.id AND status='posted')
                - (SELECT COALESCE(SUM(r.total_cents),0) FROM returns r JOIN sales s ON s.id=r.sale_id WHERE s.customer_id=c.id),
              c.email
            FROM customers c WHERE c.active=1 ORDER BY c.name
            """,
            x => new CustomerRow(x.GetInt64(0), x.GetString(1), x.IsDBNull(2) ? null : x.GetString(2), x.GetInt64(3) == 1,
                Money.FromCents(x.GetInt64(4)), Money.FromCents(x.GetInt64(5)), Money.FromCents(x.GetInt64(6)), x.IsDBNull(7) ? null : x.GetString(7)));
    }

    /// <summary>Owner-only: edit contact details (credit has its own audited method).</summary>
    public void Update(long ownerId, long customerId, string name, string? phone, string? email)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (string.IsNullOrWhiteSpace(name)) throw new PosException(PosError.InvalidPrice, "A name is required.");
            Sql.Exec(c, "UPDATE customers SET name=@n,phone=@p,email=@e WHERE id=@i", ("@n", name.Trim()), ("@p", phone), ("@e", email), ("@i", customerId));
            Common.Audit(c, clock, ownerId, null, "customer.update", customerId.ToString());
            return 0;
        });
    }

    /// <summary>Owner-only: enable credit and set the limit.</summary>
    public void SetCredit(long ownerId, long customerId, bool enabled, decimal limit)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (limit < 0) throw new PosException(PosError.InvalidPrice, "Credit limit cannot be negative.");
            Sql.Exec(c, "UPDATE customers SET credit_enabled=@e,credit_limit_cents=@l WHERE id=@i",
                ("@e", enabled ? 1 : 0), ("@l", Money.ToCents(limit)), ("@i", customerId));
            Common.Audit(c, clock, ownerId, null, "customer.credit", customerId.ToString(), null, $"enabled={enabled};limit={limit}");
            return 0;
        });
    }

    /// <summary>Ledger entries with a running balance, oldest first.</summary>
    public IReadOnlyList<StatementEntry> Statement(long customerId)
    {
        using var c = db.Open();
        var rows = Sql.Query(c,
            """
            SELECT l.created_at,l.entry_type,l.amount_cents,
              CASE l.source_type WHEN 'sale' THEN (SELECT number FROM sales WHERE id=l.source_id)
                                 WHEN 'collection' THEN (SELECT number FROM collections WHERE id=l.source_id)
                                 WHEN 'return' THEN (SELECT number FROM returns WHERE id=l.source_id)
                                 WHEN 'opening' THEN 'OPENING' END
            FROM customer_ledger l WHERE l.customer_id=@c ORDER BY l.id
            """,
            x => (At: x.GetString(0), Type: x.GetString(1), Cents: x.GetInt64(2), Doc: x.IsDBNull(3) ? null : x.GetString(3)), ("@c", customerId));
        long running = 0;
        var result = new List<StatementEntry>();
        foreach (var r in rows)
        {
            running += r.Cents;
            result.Add(new StatementEntry(r.At, r.Type, Money.FromCents(r.Cents), Money.FromCents(running), r.Doc));
        }
        return result;
    }

    /// <summary>Credit invoices that still have a remaining amount.</summary>
    public IReadOnlyList<OpenInvoice> OpenInvoices(long customerId)
    {
        using var c = db.Open();
        var sales = Sql.Query(c, "SELECT id,number,created_at,total_cents FROM sales WHERE customer_id=@c AND debt_cents>0 AND status='posted' ORDER BY created_at,id",
            x => (Id: x.GetInt64(0), Number: x.GetString(1), At: x.GetString(2), Total: x.GetInt64(3)), ("@c", customerId));
        return sales
            .Select(s => new OpenInvoice(s.Id, s.Number, s.At, Money.FromCents(s.Total), Money.FromCents(Common.SaleOutstandingCents(c, s.Id))))
            .Where(i => i.Outstanding > 0).ToList();
    }

    /// <summary>Balance is always derived from ledger entries; there is no editable balance field.</summary>
    public decimal Balance(long customerId)
    {
        using var c = db.Open();
        return Money.FromCents(Common.CustomerBalanceCents(c, customerId));
    }
}

public sealed class ShiftService(PosDatabase db, IClock clock)
{
    public long Open(long cashierId, decimal openingFloat)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireActive(c, cashierId);
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM shifts WHERE status='open'") > 0)
                throw new PosException(PosError.ShiftAlreadyOpen, "A shift is already open.");
            var now = clock.Now;
            var id = Sql.Insert(c,
                "INSERT INTO shifts(cashier_id,business_date,opened_at,opening_float_cents,status) VALUES(@c,@bd,@at,@f,'open')",
                ("@c", cashierId), ("@bd", now.ToString("yyyy-MM-dd")), ("@at", now.ToString("o")), ("@f", Money.ToCents(openingFloat)));
            Common.Audit(c, clock, cashierId, null, "shift.open", id.ToString());
            return id;
        });
    }

    /// <summary>The single open shift (survives lock/restart), or null.</summary>
    public (long Id, long CashierId)? GetOpen()
    {
        using var c = db.Open();
        var rows = Sql.Query(c, "SELECT id,cashier_id FROM shifts WHERE status='open'", r => (r.GetInt64(0), r.GetInt64(1)));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Opening float plus all recorded cash movements (sales, collections, refunds, cash-outs...).</summary>
    public decimal ExpectedCash(long shiftId)
    {
        using var c = db.Open();
        var opening = Sql.Scalar<long>(c, "SELECT opening_float_cents FROM shifts WHERE id=@s", ("@s", shiftId));
        var moves = Sql.Scalar<long>(c, "SELECT COALESCE(SUM(amount_cents),0) FROM cash_movements WHERE shift_id=@s", ("@s", shiftId));
        return Money.FromCents(opening + moves);
    }
}

public sealed class InventoryService(PosDatabase db, IClock clock)
{
    /// <summary>Owner-only delivery. Updates the weighted-average cost; optionally books a linked register cash-out exactly once.</summary>
    public long Receive(long ownerId, long productId, decimal qty, decimal unitCost, string? supplier = null,
        string? reference = null, string? documentDate = null, long? paidFromShiftId = null)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (StockTracking.Tracked(c, productId)) throw new PosException(PosError.InvalidPrice, "Receive expiry-tracked products through the lot-aware purchase invoice.");
            if (qty <= 0) throw new PosException(PosError.InvalidQuantity, "Received quantity must be positive.");
            if (unitCost < 0) throw new PosException(PosError.InvalidPrice, "Unit cost cannot be negative.");

            var avg = Money.FromCost4(Sql.Scalar<long>(c, "SELECT avg_cost_x10000 FROM products WHERE id=@p", ("@p", productId)));
            var onHand = Common.OnHand(c, productId);
            var newAvg = CostCalculator.WeightedAverage(onHand, avg, qty, unitCost);
            var stamp = Common.Stamp(clock);

            var receiptId = Sql.Insert(c,
                """
                INSERT INTO stock_receipts(product_id,qty_milli,unit_cost_x10000,supplier,reference,document_date,posted_at,actor_id,shift_id)
                VALUES(@p,@q,@u,@s,@r,@d,@t,@a,@sh)
                """,
                ("@p", productId), ("@q", Money.ToMilli(qty)), ("@u", Money.ToCost4(unitCost)), ("@s", supplier), ("@r", reference),
                ("@d", documentDate), ("@t", stamp), ("@a", ownerId), ("@sh", paidFromShiftId));

            Sql.Exec(c,
                "INSERT INTO stock_movements(product_id,qty_milli,unit_cost_x10000,source_type,source_id,actor_id,created_at) VALUES(@p,@q,@u,'receipt',@sid,@a,@t)",
                ("@p", productId), ("@q", Money.ToMilli(qty)), ("@u", Money.ToCost4(unitCost)), ("@sid", receiptId), ("@a", ownerId), ("@t", stamp));
            Sql.Exec(c, "UPDATE products SET avg_cost_x10000=@v WHERE id=@p", ("@v", Money.ToCost4(newAvg)), ("@p", productId));

            if (paidFromShiftId is long shiftId)
            {
                if (Sql.Scalar<string>(c, "SELECT status FROM shifts WHERE id=@s", ("@s", shiftId)) != "open")
                    throw new PosException(PosError.ShiftNotOpen, "A register-paid delivery needs an open shift.");
                Sql.Exec(c,
                    "INSERT INTO cash_movements(shift_id,kind,amount_cents,source_type,source_id,created_at) VALUES(@sh,'stock_purchase',@a,'receipt',@sid,@t)",
                    ("@sh", shiftId), ("@a", -Money.ToCents(qty * unitCost)), ("@sid", receiptId), ("@t", stamp));
            }

            Common.Audit(c, clock, ownerId, null, "stock.receive", productId.ToString(), reference);
            return receiptId;
        });
    }
}
