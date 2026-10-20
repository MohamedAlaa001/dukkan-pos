using LocalCashier.Application;
using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record StyleInput(
    string NameEn, string? NameAlt, string CategoryKey, string Kind, decimal BasePrice, decimal Cost, decimal TaxRate = 0m,
    int LowStockAlert = 10, string? Brand = null, string? Description = null, bool TrackInventory = true, bool AllowDiscount = true, bool Active = true,
    string Unit = "pc", bool AllowDecimal = false);

public sealed record VariantInput(string Size, string ColorNameEn, string ColorNameAlt, string ColorHex, string? Sku = null, string? Barcode = null);

public sealed record StyleRow(
    long Id, string NameEn, string? NameAlt, string CategoryKey, string Kind, decimal BasePrice, decimal TotalStock, int VariantCount,
    int LowStockAlert, bool Active, string? FirstColorHex, bool HasImage = false)
{
    public StockStatus State => TotalStock <= 0 ? StockStatus.OutOfStock : TotalStock < 10 ? StockStatus.LowStock : StockStatus.Active;
}

public enum StockStatus { Active, LowStock, OutOfStock }

public sealed record VariantRow(
    long ProductId, long StyleId, string Sku, string? Barcode, string? Size, string? ColorNameEn, string? ColorNameAlt, string? ColorHex,
    decimal Price, decimal AvgCost, decimal Stock, bool Active, string Unit = "pc", bool AllowDecimal = false);

public sealed record StyleDetail(StyleInput Input, long Id, IReadOnlyList<VariantRow> Variants);

/// <summary>Style/variant catalog for clothing: one style, many sizeÃ—color sellable rows with their own SKU, barcode and stock.</summary>
public sealed class StyleCatalogService(PosDatabase db, IClock clock)
{
    static string Slug(string s, int n) =>
        new string(s.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant() is { Length: > 0 } t ? t[..Math.Min(n, t.Length)] : "X";

    static string Prefix(string kind, long styleId) => $"{Slug(kind, 2)}{styleId:D3}";

    public long CreateStyle(long ownerId, StyleInput s, IReadOnlyList<VariantInput> variants)
    {
        using var c = db.Open();
        return Db.InTransaction(c, () => CreateStyleIn(c, ownerId, s, variants));
    }

    /// <summary>Creates a style inside the caller's transaction (used by imports so a whole file is all-or-nothing).</summary>
    internal long CreateStyleIn(SqliteConnection c, long ownerId, StyleInput s, IReadOnlyList<VariantInput> variants)
    {
        {
            Common.RequireOwner(c, ownerId);
            Validate(s);
            EnsureCategory(c, s.CategoryKey);
            var styleId = Sql.Insert(c,
                """
                INSERT INTO styles(name_en,name_alt,category,brand,description,kind,base_price_cents,cost_cents,tax_rate_bp,low_stock_milli,track_inventory,allow_discount,active,unit,allow_decimal)
                VALUES(@n,@a,@cat,@b,@d,@k,@p,@co,@t,@l,@tr,@ad,@ac,@un,@dc)
                """,
                ("@n", s.NameEn.Trim()), ("@a", s.NameAlt), ("@cat", s.CategoryKey), ("@b", s.Brand), ("@d", s.Description), ("@k", s.Kind),
                ("@p", Money.ToCents(s.BasePrice)), ("@co", Money.ToCents(s.Cost)), ("@t", decimal.ToInt64(s.TaxRate * 10000m)),
                ("@l", Money.ToMilli(s.LowStockAlert)), ("@tr", s.TrackInventory ? 1 : 0), ("@ad", s.AllowDiscount ? 1 : 0), ("@ac", s.Active ? 1 : 0),
                ("@un", s.Unit), ("@dc", s.AllowDecimal ? 1 : 0));
            InsertVariants(c, styleId, s, variants);
            Common.Audit(c, clock, ownerId, null, "style.create", styleId.ToString(), s.NameEn);
            return styleId;
        }
    }

    public void AddVariants(long ownerId, long styleId, IReadOnlyList<VariantInput> variants)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            InsertVariants(c, styleId, ReadInput(c, styleId), variants);
            Common.Audit(c, clock, ownerId, null, "style.add_variants", styleId.ToString());
            return 0;
        });
    }

    /// <summary>Updates the shared fields; every variant follows. Cost history (average cost) is never rewritten.</summary>
    public void UpdateStyle(long ownerId, long styleId, StyleInput s)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            Validate(s);
            EnsureCategory(c, s.CategoryKey);
            Sql.Exec(c,
                """
                UPDATE styles SET name_en=@n,name_alt=@a,category=@cat,brand=@b,description=@d,kind=@k,base_price_cents=@p,cost_cents=@co,tax_rate_bp=@t,
                  low_stock_milli=@l,track_inventory=@tr,allow_discount=@ad,active=@ac,unit=@un,allow_decimal=@dc WHERE id=@i
                """,
                ("@n", s.NameEn.Trim()), ("@a", s.NameAlt), ("@cat", s.CategoryKey), ("@b", s.Brand), ("@d", s.Description), ("@k", s.Kind),
                ("@p", Money.ToCents(s.BasePrice)), ("@co", Money.ToCents(s.Cost)), ("@t", decimal.ToInt64(s.TaxRate * 10000m)),
                ("@l", Money.ToMilli(s.LowStockAlert)), ("@tr", s.TrackInventory ? 1 : 0), ("@ad", s.AllowDiscount ? 1 : 0), ("@ac", s.Active ? 1 : 0), ("@un", s.Unit), ("@dc", s.AllowDecimal ? 1 : 0), ("@i", styleId));
            Sql.Exec(c,
                "UPDATE products SET name=@n,name_alt=@a,category=@cat,price_cents=@p,tax_rate_bp=@t,low_stock_milli=@l,active=@ac,unit=@un,allow_decimal=@dc WHERE style_id=@i",
                ("@n", s.NameEn.Trim()), ("@a", s.NameAlt), ("@cat", s.CategoryKey), ("@p", Money.ToCents(s.BasePrice)),
                ("@t", decimal.ToInt64(s.TaxRate * 10000m)), ("@l", Money.ToMilli(s.LowStockAlert)), ("@ac", s.Active ? 1 : 0),
                ("@un", s.Unit), ("@dc", s.AllowDecimal ? 1 : 0), ("@i", styleId));
            Common.Audit(c, clock, ownerId, null, "style.update", styleId.ToString(), s.NameEn);
            return 0;
        });
    }

    /// <summary>Archive instead of delete: history keeps referencing the rows, new sales cannot use them.</summary>
    public void Archive(long ownerId, long styleId)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            Sql.Exec(c, "UPDATE styles SET active=0 WHERE id=@i", ("@i", styleId));
            Sql.Exec(c, "UPDATE products SET active=0 WHERE style_id=@i", ("@i", styleId));
            Common.Audit(c, clock, ownerId, null, "style.archive", styleId.ToString());
            return 0;
        });
    }

    public const int MaxImageBytes = 400 * 1024;

    /// <summary>Owner-only. Stores a product photo (already resized by the caller) inside the database so it travels with backups. Null removes it.</summary>
    public void SetImage(long ownerId, long styleId, byte[]? image)
    {
        using var c = db.Open();
        Db.InTransaction(c, () =>
        {
            Common.RequireOwner(c, ownerId);
            if (image is { Length: > MaxImageBytes }) throw new PosException(PosError.InvalidPrice, "The image is too large.");
            if (image is { Length: > 0 } && !LooksLikeImage(image)) throw new PosException(PosError.InvalidPrice, "That file is not a supported image.");
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM styles WHERE id=@i", ("@i", styleId)) == 0) throw new PosException(PosError.NotFound, "Product not found.");
            Sql.Exec(c, "UPDATE styles SET image=@b WHERE id=@i", ("@b", image is { Length: > 0 } ? image : null), ("@i", styleId));
            Common.Audit(c, clock, ownerId, null, image is { Length: > 0 } ? "product.image_set" : "product.image_remove", styleId.ToString());
            return 0;
        });
    }

    static bool LooksLikeImage(byte[] b) =>
        (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||   // PNG
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF);                       // JPEG

    public byte[]? GetImage(long styleId)
    {
        using var c = db.Open();
        return Sql.Query(c, "SELECT image FROM styles WHERE id=@i", x => x.IsDBNull(0) ? null : (byte[])x[0], ("@i", styleId)).FirstOrDefault();
    }

    public IReadOnlyList<StyleRow> List(string? search = null, string? categoryKey = null, string? state = null, bool includeArchived = false, long? locationId = null)
    {
        using var c = db.Open();
        var rows = Sql.Query(c,
            """
            SELECT s.id,s.name_en,s.name_alt,COALESCE(s.category,''),s.kind,s.base_price_cents,s.low_stock_milli,s.active,
              (SELECT COALESCE(SUM(m.qty_milli),0) FROM stock_movements m JOIN products p ON p.id=m.product_id WHERE p.style_id=s.id AND (@loc IS NULL OR m.location_id=@loc)),
              (SELECT COUNT(*) FROM products p WHERE p.style_id=s.id),
              (SELECT color_hex FROM products p WHERE p.style_id=s.id ORDER BY p.id LIMIT 1),
              s.image IS NOT NULL
            FROM styles s WHERE (@arch=1 OR s.active=1) ORDER BY s.name_en
            """,
            x => new StyleRow(x.GetInt64(0), x.GetString(1), x.IsDBNull(2) ? null : x.GetString(2), x.GetString(3), x.GetString(4),
                Money.FromCents(x.GetInt64(5)), x.GetInt64(8) / 1000m, (int)x.GetInt64(9), (int)(x.GetInt64(6) / 1000), x.GetInt64(7) == 1,
                x.IsDBNull(10) ? null : x.GetString(10), x.GetInt64(11) == 1),
            ("@arch", includeArchived ? 1 : 0), ("@loc", locationId));
        IEnumerable<StyleRow> q = rows;
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(r => r.NameEn.Contains(search, StringComparison.OrdinalIgnoreCase) || (r.NameAlt?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || HasSkuOrBarcode(c, r.Id, search));
        if (!string.IsNullOrWhiteSpace(categoryKey)) q = q.Where(r => r.CategoryKey == categoryKey);
        if (!string.IsNullOrWhiteSpace(state)) q = q.Where(r => r.State.ToString() == state);
        return q.ToList();
    }

    static bool HasSkuOrBarcode(SqliteConnection c, long styleId, string text) =>
        Sql.Scalar<long>(c, "SELECT COUNT(*) FROM products WHERE style_id=@i AND (sku LIKE @t OR barcode=@e)", ("@i", styleId), ("@t", "%" + text + "%"), ("@e", text)) > 0;

    public StyleDetail? Get(long styleId)
    {
        using var c = db.Open();
        var input = TryReadInput(c, styleId);
        return input is null ? null : new StyleDetail(input, styleId, ReadVariants(c, styleId));
    }

    public IReadOnlyList<VariantRow> Variants(long styleId, long? locationId = null)
    {
        using var c = db.Open();
        return ReadVariants(c, styleId, locationId);
    }

    static IReadOnlyList<VariantRow> ReadVariants(SqliteConnection c, long styleId, long? locationId = null) =>
        Sql.Query(c,
            """
            SELECT p.id,p.style_id,p.sku,p.barcode,p.size,p.color_name_en,p.color_name_alt,p.color_hex,p.price_cents,p.avg_cost_x10000,p.active,
              COALESCE((SELECT SUM(qty_milli) FROM stock_movements WHERE product_id=p.id AND (@loc IS NULL OR location_id=@loc)),0),p.unit,p.allow_decimal
            FROM products p WHERE p.style_id=@i ORDER BY p.id
            """,
            x => new VariantRow(x.GetInt64(0), x.GetInt64(1), x.GetString(2), x.IsDBNull(3) ? null : x.GetString(3), x.IsDBNull(4) ? null : x.GetString(4),
                x.IsDBNull(5) ? null : x.GetString(5), x.IsDBNull(6) ? null : x.GetString(6), x.IsDBNull(7) ? null : x.GetString(7),
                Money.FromCents(x.GetInt64(8)), Money.FromCost4(x.GetInt64(9)), x.GetInt64(11) / 1000m, x.GetInt64(10) == 1, x.GetString(12), x.GetInt64(13) == 1),
            ("@i", styleId), ("@loc", locationId));

    static StyleInput ReadInput(SqliteConnection c, long styleId) =>
        TryReadInput(c, styleId) ?? throw new PosException(PosError.NotFound, "Style not found.");

    static StyleInput? TryReadInput(SqliteConnection c, long styleId) =>
        Sql.Query(c,
            "SELECT name_en,name_alt,COALESCE(category,''),kind,base_price_cents,cost_cents,tax_rate_bp,low_stock_milli,brand,description,track_inventory,allow_discount,active,unit,allow_decimal FROM styles WHERE id=@i",
            x => new StyleInput(x.GetString(0), x.IsDBNull(1) ? null : x.GetString(1), x.GetString(2), x.GetString(3), Money.FromCents(x.GetInt64(4)),
                Money.FromCents(x.GetInt64(5)), x.GetInt64(6) / 10000m, (int)(x.GetInt64(7) / 1000), x.IsDBNull(8) ? null : x.GetString(8),
                x.IsDBNull(9) ? null : x.GetString(9), x.GetInt64(10) == 1, x.GetInt64(11) == 1, x.GetInt64(12) == 1, x.GetString(13), x.GetInt64(14) == 1),
            ("@i", styleId)).FirstOrDefault();

    /// <summary>Any category a product uses is also a listed category, so it always appears in filters and tabs.</summary>
    static void EnsureCategory(SqliteConnection c, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Sql.Exec(c, "INSERT INTO categories(name,sort,icon) SELECT @n,(SELECT COALESCE(MAX(sort),0)+1 FROM categories),@i WHERE NOT EXISTS(SELECT 1 FROM categories WHERE lower(name)=lower(@n))", ("@n", name.Trim()), ("@i", BusinessPresets.GuessIcon(name)));
    }

    static void Validate(StyleInput s)
    {
        if (string.IsNullOrWhiteSpace(s.NameEn)) throw new PosException(PosError.InvalidPrice, "A style name is required.");
        if (s.BasePrice <= 0) throw new PosException(PosError.InvalidPrice, "The base price must be positive.");
        if (s.Cost < 0) throw new PosException(PosError.InvalidPrice, "The cost cannot be negative.");
    }

    void InsertVariants(SqliteConnection c, long styleId, StyleInput s, IReadOnlyList<VariantInput> variants)
    {
        var seen = new HashSet<(string, string)>(Sql.Query(c, "SELECT COALESCE(size,''),COALESCE(color_name_en,'') FROM products WHERE style_id=@i",
            x => (x.GetString(0), x.GetString(1)), ("@i", styleId)));
        foreach (var v in variants)
        {
            if (!seen.Add((v.Size ?? "", v.ColorNameEn ?? "")))
                throw new PosException(PosError.Duplicate, $"The variant {v.Size} / {v.ColorNameEn} already exists.");
            var sku = string.IsNullOrWhiteSpace(v.Sku)
                ? string.Join("-", new[] { Prefix(s.Kind, styleId), string.IsNullOrWhiteSpace(v.ColorNameEn) ? null : Slug(v.ColorNameEn, 3), string.IsNullOrWhiteSpace(v.Size) ? null : Slug(v.Size, 4) }.Where(x => x is not null))
                : v.Sku.Trim();
            var barcode = string.IsNullOrWhiteSpace(v.Barcode) ? NextBarcode(c) : v.Barcode.Trim();
            Sql.Insert(c,
                """
                INSERT INTO products(sku,barcode,name,name_alt,category,unit,price_cents,allow_decimal,low_stock_milli,tax_rate_bp,avg_cost_x10000,active,
                  style_id,size,color_name_en,color_name_alt,color_hex)
                VALUES(@sku,@bc,@n,@a,@cat,@un,@p,@dc,@l,@t,@co,@ac,@st,@sz,@ce,@ca,@ch)
                """,
                ("@sku", sku), ("@bc", barcode), ("@n", s.NameEn.Trim()), ("@a", s.NameAlt), ("@cat", s.CategoryKey), ("@p", Money.ToCents(s.BasePrice)),
                ("@l", Money.ToMilli(s.LowStockAlert)), ("@t", decimal.ToInt64(s.TaxRate * 10000m)), ("@co", Money.ToCost4(s.Cost)), ("@ac", s.Active ? 1 : 0),
                ("@st", styleId), ("@sz", v.Size), ("@ce", v.ColorNameEn), ("@ca", v.ColorNameAlt), ("@ch", v.ColorHex), ("@un", s.Unit), ("@dc", s.AllowDecimal ? 1 : 0));
        }
    }

    /// <summary>Next unused internal EAN-13 (sequence kept in settings so numbers are never reused).</summary>
    static string NextBarcode(SqliteConnection c)
    {
        while (true)
        {
            var n = Sql.Scalar<long?>(c, "SELECT value FROM settings WHERE key='barcode_seq'") ?? 0;
            Sql.Exec(c, "INSERT INTO settings(key,value) VALUES('barcode_seq',@v) ON CONFLICT(key) DO UPDATE SET value=@v", ("@v", (n + 1).ToString()));
            var code = Ean13.FromSequence(n + 1);
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM products WHERE barcode=@b", ("@b", code)) == 0) return code;
        }
    }
}

