using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public enum ImportKind { Products, Customers }

public sealed record ImportError(int Row, string Column, string Code, string Message);

public sealed record ImportPreview(
    ImportKind Kind, int Rows, int Styles, int Variants, decimal OpeningUnits, decimal OpeningValue, int Customers, decimal OpeningBalances,
    IReadOnlyList<ImportError> Errors, int NewCategories = 0)
{
    public bool IsValid => Errors.Count == 0 && Rows > 0;
}

public sealed record ImportResult(string Reference, int Rows, bool AlreadyApplied);

/// <summary>
/// Controlled UTF-8 CSV imports (catalog + opening stock, customers + opening balances). A file is previewed with row-specific errors,
/// never overwrites existing data, and is applied all-or-nothing under a migration reference that makes retries harmless.
/// </summary>
public sealed class ImportService(PosDatabase db, IClock clock, StyleCatalogService styles)
{
    public const string ProductHeader = "name,name_ar,category,unit,price,cost,tax_percent,option1,option2,option2_color,opening_qty,barcode";
    const string ProductRequired = "name,price";
    static readonly string[] Units = ["pc", "kg", "g", "m", "l", "box", "pack"];
    static readonly string[] DecimalUnits = ["kg", "g", "m", "l"];
    public const string CustomerHeader = "name,phone,credit_limit,opening_balance";

    static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    /// <summary>A template with one example row. Text columns that look like numbers (barcode, phone) must stay text when opened in a spreadsheet.</summary>
    public static string Template(ImportKind kind) => kind == ImportKind.Products
        ? ProductHeader + "\r\nPolo Shirt,قميص بولو,Shirts,pc,300,150,0,M,Navy,#1B3F73,12,\r\nBasmati rice,أرز بسمتي,Produce,kg,45,30,0,,,,25,\r\n"
        : CustomerHeader + "\r\nSara Mohamed,01098765432,5000,0\r\n";

    // ------------------------------------------------------------------ CSV parsing
    /// <summary>RFC-4180 parsing: quotes, doubled quotes, newlines inside quotes, optional BOM. Returns rows of cells.</summary>
    public static List<string[]> ParseCsv(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Any(c => c.Length > 0) || row.Count > 1) rows.Add(row.ToArray());
                row = [];
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            if (row.Any(c => c.Length > 0)) rows.Add(row.ToArray());
        }
        return rows;
    }

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    sealed record ProductRow(int Line, string Name, string? NameAr, string Category, string Unit, decimal Price, decimal Cost, decimal Tax,
        string Size, string Color, string ColorHex, decimal Qty, string? Barcode);

    sealed record CustomerRow(int Line, string Name, string? Phone, decimal Limit, decimal Opening);

    // ------------------------------------------------------------------ onboarding
    public bool IsOnboardingOpen()
    {
        using var c = db.Open();
        return Sql.Scalar<string>(c, "SELECT value FROM settings WHERE key='onboarding_closed'") != "1";
    }

    /// <summary>Owner-only and permanent: afterwards imports can no longer create opening stock or opening balances.</summary>
    public void CloseOnboarding(long ownerId)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            Sql.Exec(c, "INSERT INTO settings(key,value) VALUES('onboarding_closed','1') ON CONFLICT(key) DO UPDATE SET value='1'");
            Common.Audit(c, clock, ownerId, null, "import.close_onboarding", null);
            return 0;
        });
    }

    // ------------------------------------------------------------------ preview
    public ImportPreview Preview(ImportKind kind, string csv)
    {
        using var c = db.Open();
        var onboardingOpen = Sql.Scalar<string>(c, "SELECT value FROM settings WHERE key='onboarding_closed'") != "1";
        return kind == ImportKind.Products ? PreviewProducts(c, csv, onboardingOpen, out _) : PreviewCustomers(c, csv, onboardingOpen, out _);
    }

    static ImportPreview Fail(ImportKind kind, int row, string col, string code, string msg) =>
        new(kind, 0, 0, 0, 0, 0, 0, 0, [new ImportError(row, col, code, msg)]);

    static Dictionary<string, int>? Header(List<string[]> rows, string expected, out ImportError? error)
    {
        error = null;
        if (rows.Count == 0) { error = new ImportError(1, "", "empty", "The file is empty."); return null; }
        var map = rows[0].Select((h, i) => (h: h.Trim().ToLowerInvariant(), i)).Where(x => x.h.Length > 0).GroupBy(x => x.h).ToDictionary(g => g.Key, g => g.First().i);
        // Files made for the earlier clothing-only template keep working.
        foreach (var (old, now) in new[] { ("size", "option1"), ("color", "option2"), ("color_hex", "option2_color") })
            if (map.TryGetValue(old, out var oi) && !map.ContainsKey(now)) map[now] = oi;
        var missing = expected.Split(',').Where(h => !map.ContainsKey(h)).ToList();
        if (missing.Count > 0) { error = new ImportError(1, string.Join(", ", missing), "header", "Missing column(s): " + string.Join(", ", missing)); return null; }
        return map;
    }

    static bool Amount(string s, out decimal v) =>
        decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v) && v >= 0 && v == decimal.Round(v, 2);

    ImportPreview PreviewProducts(SqliteConnection c, string csv, bool onboardingOpen, out List<ProductRow> parsed)
    {
        parsed = [];
        var rows = ParseCsv(csv);
        var map = Header(rows, ProductRequired, out var herr);
        if (map is null) return Fail(ImportKind.Products, herr!.Row, herr.Column, herr.Code, herr.Message);

        var errors = new List<ImportError>();
        var existingNames = Sql.Query(c, "SELECT lower(name_en) FROM styles", x => x.GetString(0)).ToHashSet();
        var existingBarcodes = Sql.Query(c, "SELECT barcode FROM products WHERE barcode IS NOT NULL", x => x.GetString(0)).ToHashSet();
        var seenVariants = new HashSet<string>();
        var seenBarcodes = new HashSet<string>();
        var firstOfStyle = new Dictionary<string, ProductRow>();
        var cfg = new CatalogConfigService(db, clock).Get();
        var knownCategories = Sql.Query(c, "SELECT name FROM categories WHERE active=1 ORDER BY sort,name", x => x.GetString(0)).ToDictionary(n => n, StringComparer.OrdinalIgnoreCase);
        var defaultCategory = knownCategories.Keys.FirstOrDefault() ?? "Others";
        var newCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var r = 1; r < rows.Count; r++)
        {
            var line = r + 1;
            string Cell(string col) => map.TryGetValue(col, out var idx) && idx < rows[r].Length ? rows[r][idx].Trim() : "";
            void Err(string col, string code, string msg) => errors.Add(new ImportError(line, col, code, msg));
            var before = errors.Count;

            var name = Cell("name");
            if (name.Length == 0) Err("name", "required", "Name is required.");
            var size = Cell("option1"); var color = Cell("option2");
            if (!Amount(Cell("price"), out var price) || price <= 0) Err("price", "invalid_amount", "Price must be a positive amount with at most two decimals.");
            decimal cost = 0;
            if (Cell("cost").Length > 0 && !Amount(Cell("cost"), out cost)) Err("cost", "invalid_amount", "Cost must be an amount with at most two decimals.");
            decimal tax = 0;
            if (Cell("tax_percent").Length > 0 && (!Amount(Cell("tax_percent"), out tax) || tax > 100)) Err("tax_percent", "invalid_amount", "Tax must be between 0 and 100.");
            var category = Cell("category").Length == 0 ? defaultCategory : (knownCategories.TryGetValue(Cell("category"), out var canon) ? canon : Cell("category"));
            if (!knownCategories.ContainsKey(category)) newCategories.Add(category);
            var unit = Cell("unit").Length == 0 ? "pc" : Cell("unit").ToLowerInvariant();
            if (!Units.Contains(unit)) Err("unit", "invalid_value", "Unit must be one of: " + string.Join(", ", Units));
            var hex = Cell("option2_color");
            if (hex.Length == 0) hex = color.Length > 0 && cfg.Option2IsColor ? "#9CA3AF" : "";
            else if (!Hex.IsMatch(hex)) Err("option2_color", "invalid_value", "Color must look like #1B3F73.");
            decimal qty = 0;
            var decimalUnit = DecimalUnits.Contains(unit);
            if (Cell("opening_qty").Length > 0 && (!decimal.TryParse(Cell("opening_qty"), NumberStyles.Number, CultureInfo.InvariantCulture, out qty) || qty < 0 || qty > 1_000_000
                || qty != decimal.Round(qty, 3) || (!decimalUnit && qty != decimal.Truncate(qty))))
                Err("opening_qty", "invalid_quantity", decimalUnit ? "Opening quantity must be zero or more (up to three decimals)." : "Opening quantity must be a whole number, zero or more.");
            if (qty > 0 && !onboardingOpen) Err("opening_qty", "onboarding_closed", "Onboarding is closed: opening stock can no longer be imported.");
            if (qty > 0 && cost <= 0) Err("cost", "stock_needs_cost", "Opening stock needs a unit cost.");
            var barcode = Cell("barcode");
            if (barcode.Length > 0)
            {
                if (!Regex.IsMatch(barcode, @"^\d{8,14}$")) Err("barcode", "invalid_value", "Barcode must be 8 to 14 digits.");
                else if (existingBarcodes.Contains(barcode)) Err("barcode", "duplicate_existing", "This barcode already belongs to a product.");
                else if (!seenBarcodes.Add(barcode)) Err("barcode", "duplicate_file", "This barcode appears twice in the file.");
            }

            if (name.Length > 0)
            {
                var key = name.ToLowerInvariant();
                if (existingNames.Contains(key)) Err("name", "duplicate_existing", "A product with this name already exists. Imports never overwrite.");
                if (!seenVariants.Add($"{key}|{size.ToLowerInvariant()}|{color.ToLowerInvariant()}")) Err("size", "duplicate_file", "This size/color appears twice for the same product.");
                if (errors.Count == before && firstOfStyle.TryGetValue(key, out var first) && (first.Price != price || first.Cost != cost || first.Category != category || first.Unit != unit))
                    Err("price", "inconsistent", "Price, cost, category and unit must match the first row of this product.");
            }

            if (errors.Count == before)
            {
                var row = new ProductRow(line, name, Cell("name_ar").Length == 0 ? null : Cell("name_ar"), category, unit, price, cost, tax, size, color, hex, qty, barcode.Length == 0 ? null : barcode);
                parsed.Add(row);
                firstOfStyle.TryAdd(name.ToLowerInvariant(), row);
            }
        }

        return new ImportPreview(ImportKind.Products, rows.Count - 1, parsed.Select(p => p.Name.ToLowerInvariant()).Distinct().Count(), parsed.Count,
            parsed.Sum(p => p.Qty), parsed.Sum(p => p.Qty * p.Cost), 0, 0, errors, newCategories.Count);
    }

    ImportPreview PreviewCustomers(SqliteConnection c, string csv, bool onboardingOpen, out List<CustomerRow> parsed)
    {
        parsed = [];
        var rows = ParseCsv(csv);
        var map = Header(rows, CustomerHeader, out var herr);
        if (map is null) return Fail(ImportKind.Customers, herr!.Row, herr.Column, herr.Code, herr.Message);

        var errors = new List<ImportError>();
        var existing = Sql.Query(c, "SELECT lower(name)||'|'||COALESCE(phone,'') FROM customers", x => x.GetString(0)).ToHashSet();
        var seen = new HashSet<string>();
        for (var r = 1; r < rows.Count; r++)
        {
            var line = r + 1;
            string Cell(string col) => map[col] < rows[r].Length ? rows[r][map[col]].Trim() : "";
            void Err(string col, string code, string msg) => errors.Add(new ImportError(line, col, code, msg));
            var before = errors.Count;
            var name = Cell("name");
            if (name.Length == 0) Err("name", "required", "Name is required.");
            var phone = Cell("phone");
            decimal limit = 0, opening = 0;
            if (Cell("credit_limit").Length > 0 && !Amount(Cell("credit_limit"), out limit)) Err("credit_limit", "invalid_amount", "Credit limit must be an amount with at most two decimals.");
            if (Cell("opening_balance").Length > 0 && !Amount(Cell("opening_balance"), out opening)) Err("opening_balance", "invalid_amount", "Opening balance must be an amount with at most two decimals.");
            if (opening > 0 && !onboardingOpen) Err("opening_balance", "onboarding_closed", "Onboarding is closed: opening balances can no longer be imported.");
            if (name.Length > 0)
            {
                var key = name.ToLowerInvariant() + "|" + phone;
                if (existing.Contains(key)) Err("name", "duplicate_existing", "This customer already exists. Imports never overwrite.");
                else if (!seen.Add(key)) Err("name", "duplicate_file", "This customer appears twice in the file.");
            }
            if (errors.Count == before) parsed.Add(new CustomerRow(line, name, phone.Length == 0 ? null : phone, limit, opening));
        }
        return new ImportPreview(ImportKind.Customers, rows.Count - 1, 0, 0, 0, 0, parsed.Count, parsed.Sum(p => p.Opening), errors);
    }

    // ------------------------------------------------------------------ apply
    /// <summary>
    /// Owner-only. Re-validates the file, then writes everything in one transaction. The migration reference is required and unique:
    /// re-running the same file with the same reference changes nothing; a different file under a used reference is refused.
    /// </summary>
    public ImportResult Apply(long ownerId, ImportKind kind, string csv, string reference)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (string.IsNullOrWhiteSpace(reference)) throw new PosException(PosError.InvalidDiscount, "A migration reference is required.");
            reference = reference.Trim();
            var hash = Hash(csv);
            var prior = Sql.Query(c, "SELECT file_hash,row_count FROM import_batches WHERE reference=@r", x => (Hash: x.GetString(0), Rows: x.GetInt32(1)), ("@r", reference)).FirstOrDefault();
            if (prior.Hash is not null)
            {
                if (prior.Hash == hash) return new ImportResult(reference, prior.Rows, true);
                throw new PosException(PosError.Duplicate, "This migration reference was already used for a different file.");
            }

            var open = Sql.Scalar<string>(c, "SELECT value FROM settings WHERE key='onboarding_closed'") != "1";
            var stamp = Common.Stamp(clock);
            int count;
            if (kind == ImportKind.Products)
            {
                var preview = PreviewProducts(c, csv, open, out var parsed);
                if (!preview.IsValid) throw ImportFailed(preview);
                foreach (var g in parsed.GroupBy(p => p.Name.ToLowerInvariant()))
                {
                    var first = g.First();
                    var input = new StyleInput(first.Name, first.NameAr, first.Category, "TShirt", first.Price, first.Cost, first.Tax / 100m, Unit: first.Unit, AllowDecimal: DecimalUnits.Contains(first.Unit));
                    EnsureCategory(c, first.Category);
                    var variants = g.Select(p => new VariantInput(p.Size, p.Color, p.Color, p.ColorHex, null, p.Barcode)).ToList();
                    var styleId = styles.CreateStyleIn(c, ownerId, input, variants);
                    var created = Sql.Query(c, "SELECT id,size,color_name_en FROM products WHERE style_id=@s", x => (Id: x.GetInt64(0), Size: x.GetString(1), Color: x.GetString(2)), ("@s", styleId));
                    foreach (var p in g.Where(p => p.Qty > 0))
                    {
                        var pid = created.First(v => v.Size == p.Size && v.Color == p.Color).Id;
                        OpeningStock(c, ownerId, pid, p.Qty, p.Cost, reference, stamp);
                    }
                }
                count = parsed.Count;
            }
            else
            {
                var preview = PreviewCustomers(c, csv, open, out var parsed);
                if (!preview.IsValid) throw ImportFailed(preview);
                var n = 0;
                foreach (var p in parsed)
                {
                    var id = Sql.Insert(c, "INSERT INTO customers(name,phone,credit_enabled,credit_limit_cents,created_at) VALUES(@n,@p,@ce,@cl,@t)",
                        ("@n", p.Name), ("@p", p.Phone), ("@ce", p.Limit > 0 ? 1 : 0), ("@cl", Money.ToCents(p.Limit)), ("@t", stamp));
                    if (p.Opening > 0)
                    {
                        var ob = Sql.Insert(c, "INSERT INTO customer_opening_balances(operation_id,customer_id,amount_cents,entry_date,note,actor_id,created_at) VALUES(@o,@c,@a,@d,@nt,@u,@t)",
                            ("@o", $"import:{reference}:{++n}"), ("@c", id), ("@a", Money.ToCents(p.Opening)), ("@d", Common.Today(clock)),
                            ("@nt", "Import " + reference), ("@u", ownerId), ("@t", stamp));
                        Sql.Exec(c, "INSERT INTO customer_ledger(customer_id,entry_type,amount_cents,source_type,source_id,created_at) VALUES(@c,'opening',@a,'opening',@s,@t)",
                            ("@c", id), ("@a", Money.ToCents(p.Opening)), ("@s", ob), ("@t", stamp));
                    }
                }
                count = parsed.Count;
            }

            Sql.Exec(c, "INSERT INTO import_batches(reference,kind,file_hash,row_count,actor_id,created_at) VALUES(@r,@k,@h,@n,@a,@t)",
                ("@r", reference), ("@k", kind.ToString()), ("@h", hash), ("@n", count), ("@a", ownerId), ("@t", stamp));
            Common.Audit(c, clock, ownerId, null, "import.apply", reference, null, $"kind={kind};rows={count}");
            return new ImportResult(reference, count, false);
        });
    }

    static PosException ImportFailed(ImportPreview p)
    {
        var e = p.Errors.FirstOrDefault();
        return new PosException(PosError.InvalidPrice, p.Rows == 0 && e is null ? "The file has no data rows." : $"Import refused: row {e?.Row}, {e?.Column}: {e?.Message} ({p.Errors.Count} problem(s); nothing was changed)");
    }

    static void EnsureCategory(SqliteConnection c, string name) =>
        Sql.Exec(c, "INSERT INTO categories(name,sort,icon) SELECT @n,(SELECT COALESCE(MAX(sort),0)+1 FROM categories),@i WHERE NOT EXISTS(SELECT 1 FROM categories WHERE lower(name)=lower(@n))", ("@n", name), ("@i", BusinessPresets.GuessIcon(name)));

    static void OpeningStock(SqliteConnection c, long actor, long productId, decimal qty, decimal cost, string reference, string stamp)
    {
        var receipt = Sql.Insert(c,
            "INSERT INTO stock_receipts(product_id,qty_milli,unit_cost_x10000,supplier,reference,document_date,posted_at,actor_id) VALUES(@p,@q,@u,'Opening stock',@r,@d,@t,@a)",
            ("@p", productId), ("@q", Money.ToMilli(qty)), ("@u", Money.ToCost4(cost)), ("@r", reference), ("@d", stamp[..10]), ("@t", stamp), ("@a", actor));
        Sql.Exec(c, "INSERT INTO stock_movements(product_id,qty_milli,unit_cost_x10000,source_type,source_id,reason,actor_id,created_at) VALUES(@p,@q,@u,'receipt',@s,@r,@a,@t)",
            ("@p", productId), ("@q", Money.ToMilli(qty)), ("@u", Money.ToCost4(cost)), ("@s", receipt), ("@r", "Opening stock " + reference), ("@a", actor), ("@t", stamp));
        Sql.Exec(c, "UPDATE products SET avg_cost_x10000=@v WHERE id=@p", ("@v", Money.ToCost4(cost)), ("@p", productId));
    }
}
