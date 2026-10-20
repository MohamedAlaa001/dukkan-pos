using LocalCashier.Domain;
using Microsoft.Data.Sqlite;

namespace LocalCashier.Persistence;

public sealed record CatalogConfig(
    string BusinessType, string? Option1Name, string? Option2Name, bool Option2IsColor,
    IReadOnlyList<string> Option1Presets, IReadOnlyList<string> Option2Presets)
{
    public bool HasOption1 => !string.IsNullOrWhiteSpace(Option1Name);
    public bool HasOption2 => !string.IsNullOrWhiteSpace(Option2Name);
}

public sealed record CategoryRow(long Id, string Name, string? NameAlt, bool Active, string? Icon = null);

public sealed record PresetCategory(string En, string Ar, string Icon);

public sealed record BusinessPreset(
    string Type, string? Option1En, string? Option1Ar, string? Option2En, string? Option2Ar, bool Option2IsColor,
    string[] Option1Values, string[] Option2Values, PresetCategory[] Categories);

/// <summary>The shop types offered at setup. Everything a preset sets stays editable afterwards.</summary>
public static class BusinessPresets
{
    static PresetCategory C(string en, string ar, string icon) => new(en, ar, icon);

    public static readonly IReadOnlyList<BusinessPreset> All =
    [
        new("general", null, null, null, null, false, [], [],
        [
            C("General", "عام", "basket"), C("Food", "أغذية", "apple"), C("Drinks", "مشروبات", "drink"), C("Household", "مستلزمات منزلية", "cleaning"),
            C("Electronics", "إلكترونيات", "laptop"), C("Stationery", "أدوات مكتبية", "stationery"), C("Toys", "ألعاب", "toy"),
            C("Tools & hardware", "عدد وأدوات", "tool"), C("Gifts", "هدايا", "gift"), C("Others", "أخرى", "basket"),
        ]),
        new("clothing", "Size", "المقاس", "Color", "اللون", true,
            ["XS", "S", "M", "L", "XL", "XXL", "28", "30", "32", "34", "36", "38", "One size"], [],
        [
            C("TShirts", "تيشيرتات", "shirt"), C("Pants", "بناطيل", "jeans"), C("Jackets", "جواكت", "coat"), C("Hoodies", "هوديز", "coat"), C("Shirts", "قمصان", "shirt"),
            C("Dresses", "فساتين", "dress"), C("Shoes", "أحذية", "shoe"), C("Socks & underwear", "جوارب وملابس داخلية", "socks"), C("Hats & caps", "قبعات وكابات", "hat"),
            C("Bags", "حقائب", "handbag"), C("Accessories", "إكسسوارات", "jewelry"), C("Watches", "ساعات", "watch"), C("Eyewear", "نظارات", "glasses"),
            C("Scarves", "أوشحة", "scarf"), C("Others", "أخرى", "gift"),
        ]),
        new("grocery", "Pack size", "حجم العبوة", null, null, false, ["250 g", "500 g", "1 kg", "1 L"], [],
        [
            C("Produce", "خضروات", "vegetable"), C("Fruit", "فاكهة", "apple"), C("Dairy", "ألبان", "milk"), C("Bakery", "مخبوزات", "bread"), C("Meat & poultry", "لحوم ودواجن", "meat"),
            C("Fish", "أسماك", "fish"), C("Eggs", "بيض", "eggs"), C("Cheese & deli", "أجبان ومأكولات باردة", "cheese"), C("Drinks", "مشروبات", "drink"),
            C("Coffee & tea", "قهوة وشاي", "coffee"), C("Snacks & sweets", "وجبات خفيفة وحلويات", "sweets"), C("Frozen", "مجمدات", "icecream"),
            C("Rice & grains", "أرز وحبوب", "grain"), C("Spices", "توابل", "spices"), C("Cleaning", "منظفات", "cleaning"), C("Pet food", "أغذية حيوانات أليفة", "pet"), C("Others", "أخرى", "basket"),
        ]),
        new("pharmacy", "Pack", "العبوة", null, null, false, ["Strip", "Box", "Bottle"], [],
        [
            C("Medicines", "أدوية", "medicine"), C("Vitamins", "فيتامينات", "pharmacy"), C("First aid", "إسعافات أولية", "bandage"), C("Personal care", "عناية شخصية", "cosmetics"),
            C("Skin care", "عناية بالبشرة", "perfume"), C("Baby", "أطفال", "babybottle"), C("Medical devices", "أجهزة طبية", "battery"), C("Equipment", "أجهزة ومستلزمات", "tool"), C("Others", "أخرى", "gift"),
        ]),
        new("electronics", "Model", "الموديل", null, null, false, [], [],
        [
            C("Phones", "هواتف", "phone"), C("Audio", "سماعات وصوتيات", "headphones"), C("TV & video", "تلفزيونات", "tv"), C("Cameras", "كاميرات", "camera"),
            C("Computers", "كمبيوتر", "laptop"), C("Batteries & chargers", "بطاريات وشواحن", "battery"), C("Cables & accessories", "كابلات وملحقات", "key"),
            C("Lighting", "إضاءة", "bulb"), C("Tools", "عدد", "tool"), C("Others", "أخرى", "gift"),
        ]),
        new("beauty", "Size", "الحجم", null, null, false, ["30 ml", "50 ml", "100 ml", "200 ml"], [],
        [
            C("Perfume", "عطور", "perfume"), C("Makeup", "مكياج", "cosmetics"), C("Skin care", "عناية بالبشرة", "cosmetics"), C("Hair care", "عناية بالشعر", "scissors"),
            C("Jewelry", "مجوهرات", "jewelry"), C("Watches", "ساعات", "watch"), C("Eyewear", "نظارات", "glasses"), C("Bags", "حقائب", "handbag"),
            C("Flowers", "ورود", "flowers"), C("Gifts", "هدايا", "gift"), C("Others", "أخرى", "basket"),
        ]),
        new("home", null, null, null, null, false, [], [],
        [
            C("Furniture", "أثاث", "sofa"), C("Kitchen", "مطبخ", "pot"), C("Cleaning", "منظفات", "cleaning"), C("Decor & candles", "ديكور وشموع", "candle"),
            C("Garden & plants", "حديقة ونباتات", "plant"), C("Paint", "دهانات", "paint"), C("Tools & hardware", "عدد وأدوات", "tool"), C("Lighting", "إضاءة", "bulb"),
            C("Bedding & textiles", "مفروشات", "scarf"), C("Keys & locks", "مفاتيح وأقفال", "key"), C("Others", "أخرى", "basket"),
        ]),
        new("toys", null, null, null, null, false, [], [],
        [
            C("Toys", "ألعاب", "toy"), C("Books", "كتب", "book"), C("Stationery", "أدوات مكتبية", "stationery"), C("Sports", "رياضة", "ball"), C("Bikes", "دراجات", "bike"),
            C("Baby", "مستلزمات أطفال", "babybottle"), C("School bags", "حقائب مدرسية", "handbag"), C("Party & gifts", "حفلات وهدايا", "gift"), C("Umbrellas & travel", "شمسيات وسفر", "luggage"), C("Others", "أخرى", "basket"),
        ]),
    ];

    public static BusinessPreset Get(string type) => All.FirstOrDefault(p => p.Type == type) ?? All[0];

    static readonly (string Key, string Icon)[] Keywords =
    [
        ("shoe", "shoe"), ("sneaker", "shoe"), ("boot", "shoe"), ("shirt", "shirt"), ("polo", "shirt"), ("jean", "jeans"), ("pant", "jeans"), ("trouser", "jeans"),
        ("jacket", "coat"), ("coat", "coat"), ("hood", "coat"), ("dress", "dress"), ("sock", "socks"), ("hat", "hat"), ("cap", "hat"), ("bag", "handbag"),
        ("watch", "watch"), ("glass", "glasses"), ("jewel", "jewelry"), ("scarf", "scarf"), ("perfume", "perfume"), ("makeup", "cosmetics"), ("cosmetic", "cosmetics"),
        ("phone", "phone"), ("mobile", "phone"), ("audio", "headphones"), ("headphone", "headphones"), ("tv", "tv"), ("camera", "camera"), ("computer", "laptop"), ("laptop", "laptop"),
        ("battery", "battery"), ("light", "bulb"), ("tool", "tool"), ("paint", "paint"), ("furniture", "sofa"), ("kitchen", "pot"), ("candle", "candle"), ("plant", "plant"),
        ("garden", "plant"), ("toy", "toy"), ("book", "book"), ("stationery", "stationery"), ("sport", "ball"), ("bike", "bike"), ("car", "car"), ("baby", "babybottle"),
        ("medic", "medicine"), ("vitamin", "pharmacy"), ("pharm", "pharmacy"), ("aid", "bandage"), ("milk", "milk"), ("dairy", "milk"), ("bread", "bread"), ("bakery", "bread"),
        ("meat", "meat"), ("fish", "fish"), ("egg", "eggs"), ("cheese", "cheese"), ("coffee", "coffee"), ("tea", "coffee"), ("drink", "drink"), ("juice", "drink"),
        ("sweet", "sweets"), ("snack", "sweets"), ("frozen", "icecream"), ("ice", "icecream"), ("rice", "grain"), ("grain", "grain"), ("spice", "spices"),
        ("clean", "cleaning"), ("pet", "pet"), ("fruit", "apple"), ("vegetable", "vegetable"), ("produce", "vegetable"), ("flower", "flowers"), ("gift", "gift"),
        ("umbrella", "umbrella"), ("luggage", "luggage"), ("travel", "luggage"), ("key", "key"),
    ];

    /// <summary>A sensible picture for a category the owner did not pick one for: a preset match, then a keyword, then the basket.</summary>
    public static string GuessIcon(string? name)
    {
        var n = (name ?? "").Trim();
        foreach (var p in All) foreach (var c in p.Categories) if (string.Equals(c.En, n, StringComparison.OrdinalIgnoreCase)) return c.Icon;
        var lower = n.ToLowerInvariant();
        foreach (var (key, icon) in Keywords) if (lower.Contains(key)) return icon;
        return "basket";
    }

    /// <summary>The icon names bundled with the app (Assets/Icons), in the order shown in the picker.</summary>
    public static readonly IReadOnlyList<string> IconNames =
    [
        "basket", "apple", "vegetable", "meat", "fish", "eggs", "cheese", "milk", "bread", "drink", "coffee", "sweets", "icecream", "grain", "spices",
        "cleaning", "pet", "pharmacy", "medicine", "bandage", "babybottle", "cosmetics", "perfume", "scissors", "shirt", "jeans", "coat", "dress", "shoe",
        "socks", "hat", "handbag", "jewelry", "watch", "glasses", "scarf", "phone", "headphones", "tv", "camera", "laptop", "battery", "bulb", "key",
        "tool", "paint", "sofa", "pot", "candle", "plant", "toy", "book", "stationery", "ball", "bike", "car", "umbrella", "luggage", "flowers", "gift",
    ];
}

/// <summary>Shop-wide catalog settings: the names of the optional variant options (e.g. Size/Color) and the category list.</summary>
public sealed class CatalogConfigService(PosDatabase db, IClock clock)
{
    const char Sep = '|';

    /// <summary>A shop that never chose a type (installed before this feature) behaves exactly as before: Size and Color.</summary>
    public CatalogConfig Get()
    {
        using var c = db.Open();
        string? V(string key) => Sql.Scalar<string>(c, "SELECT value FROM settings WHERE key=@k", ("@k", key));
        var type = V("business_type");
        if (type is null)
        {
            var p = BusinessPresets.Get("clothing");
            return new CatalogConfig("clothing", p.Option1En, p.Option2En, true, p.Option1Values, []);
        }
        string[] Split(string? s) => string.IsNullOrEmpty(s) ? [] : s.Split(Sep, StringSplitOptions.RemoveEmptyEntries);
        var n1 = V("option1_name"); var n2 = V("option2_name");
        return new CatalogConfig(type, string.IsNullOrWhiteSpace(n1) ? null : n1, string.IsNullOrWhiteSpace(n2) ? null : n2,
            V("option2_kind") == "color", Split(V("option1_values")), Split(V("option2_values")));
    }

    /// <summary>Owner-only. Saves option names and suggestions. Existing variants keep their stored values.</summary>
    public void Save(long ownerId, CatalogConfig cfg)
    {
        using var c = db.Open();
        Db.InOwnerTransaction(c, () => { Write(c, cfg); Common.Audit(c, clock, ownerId, null, "catalog.config", cfg.BusinessType); return 0; }, ownerId);
    }

    static void Put(SqliteConnection c, string key, string? value) =>
        Sql.Exec(c, "INSERT INTO settings(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=@v", ("@k", key), ("@v", value ?? ""));

    static void Write(SqliteConnection c, CatalogConfig cfg)
    {
        Put(c, "business_type", cfg.BusinessType);
        Put(c, "option1_name", cfg.Option1Name?.Trim());
        Put(c, "option2_name", cfg.Option2Name?.Trim());
        Put(c, "option2_kind", cfg.Option2IsColor && cfg.HasOption2 ? "color" : "text");
        Put(c, "option1_values", string.Join(Sep, cfg.Option1Presets.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct()));
        Put(c, "option2_values", string.Join(Sep, cfg.Option2Presets.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct()));
    }

    /// <summary>Owner-only. Applies a shop type: the product option names and suggestions only. No categories are added.</summary>
    public void ApplyPreset(long ownerId, string type, bool arabic)
    {
        using var c = db.Open();
        Db.InOwnerTransaction(c, () =>
        {
            var p = BusinessPresets.Get(type);
            Write(c, new CatalogConfig(p.Type, arabic ? p.Option1Ar : p.Option1En, arabic ? p.Option2Ar : p.Option2En, p.Option2IsColor, p.Option1Values, p.Option2Values));
            // Categories are never created for the shop: the owner adds exactly the ones they need.
            Common.Audit(c, clock, ownerId, null, "catalog.preset", type);
            return 0;
        }, ownerId);
    }

    // ---------------- categories ----------------
    public IReadOnlyList<CategoryRow> Categories(bool includeArchived = false)
    {
        using var c = db.Open();
        return Sql.Query(c, "SELECT id,name,name_alt,active,icon FROM categories WHERE (@a=1 OR active=1) ORDER BY sort,name",
            x => new CategoryRow(x.GetInt64(0), x.GetString(1), x.IsDBNull(2) ? null : x.GetString(2), x.GetInt64(3) == 1, x.IsDBNull(4) ? null : x.GetString(4)),
            ("@a", includeArchived ? 1 : 0));
    }

    static string? CleanIcon(string? icon) =>
        string.IsNullOrWhiteSpace(icon) ? null : BusinessPresets.IconNames.Contains(icon.Trim()) ? icon.Trim() : throw new PosException(PosError.InvalidPrice, "Unknown picture.");

    public long AddCategory(long ownerId, string name, string? nameAlt = null, string? icon = null)
    {
        using var c = db.Open();
        return Db.InOwnerTransaction(c, () =>
        {
            if (string.IsNullOrWhiteSpace(name)) throw new PosException(PosError.InvalidPrice, "A category name is required.");
            name = name.Trim();
            if (Sql.Scalar<long>(c, "SELECT COUNT(*) FROM categories WHERE lower(name)=lower(@n)", ("@n", name)) > 0)
                throw new PosException(PosError.Duplicate, "That category already exists.");
            var id = Sql.Insert(c, "INSERT INTO categories(name,name_alt,icon,sort) VALUES(@n,@a,@i,(SELECT COALESCE(MAX(sort),0)+1 FROM categories))",
                ("@n", name), ("@a", string.IsNullOrWhiteSpace(nameAlt) ? null : nameAlt.Trim()), ("@i", CleanIcon(icon)));
            Common.Audit(c, clock, ownerId, null, "category.add", name);
            return id;
        }, ownerId);
    }

    /// <summary>Renames a category everywhere it is used and optionally changes its picture ("" removes it); history is unaffected.</summary>
    public void RenameCategory(long ownerId, long id, string name, string? nameAlt, string? icon = null)
    {
        using var c = db.Open();
        Db.InOwnerTransaction(c, () =>
        {
            if (string.IsNullOrWhiteSpace(name)) throw new PosException(PosError.InvalidPrice, "A category name is required.");
            name = name.Trim();
            var old = Sql.Scalar<string>(c, "SELECT name FROM categories WHERE id=@i", ("@i", id)) ?? throw new PosException(PosError.NotFound, "Category not found.");
            if (!string.Equals(old, name, StringComparison.Ordinal) && Sql.Scalar<long>(c, "SELECT COUNT(*) FROM categories WHERE lower(name)=lower(@n) AND id<>@i", ("@n", name), ("@i", id)) > 0)
                throw new PosException(PosError.Duplicate, "That category already exists.");
            Sql.Exec(c, "UPDATE categories SET name=@n,name_alt=@a WHERE id=@i", ("@n", name), ("@a", string.IsNullOrWhiteSpace(nameAlt) ? null : nameAlt.Trim()), ("@i", id));
            if (icon is not null) Sql.Exec(c, "UPDATE categories SET icon=@ic WHERE id=@i", ("@ic", CleanIcon(icon)), ("@i", id));
            Sql.Exec(c, "UPDATE styles SET category=@n WHERE category=@o", ("@n", name), ("@o", old));
            Sql.Exec(c, "UPDATE products SET category=@n WHERE category=@o", ("@n", name), ("@o", old));
            Common.Audit(c, clock, ownerId, null, "category.rename", old, null, name);
            return 0;
        }, ownerId);
    }

    /// <summary>Archive instead of delete; products keep their category text.</summary>
    public void SetCategoryActive(long ownerId, long id, bool active)
    {
        using var c = db.Open();
        Db.InOwnerTransaction(c, () =>
        {
            Sql.Exec(c, "UPDATE categories SET active=@a WHERE id=@i", ("@a", active ? 1 : 0), ("@i", id));
            Common.Audit(c, clock, ownerId, null, active ? "category.restore" : "category.archive", id.ToString());
            return 0;
        }, ownerId);
    }

    /// <summary>How many products and styles still use the category (archived or not).</summary>
    public int CategoryUsage(long id)
    {
        using var c = db.Open();
        return UsageCount(c, id);
    }

    static int UsageCount(SqliteConnection c, long id) =>
        (int)(Sql.Scalar<long>(c, "SELECT COUNT(*) FROM styles WHERE category=(SELECT name FROM categories WHERE id=@i)", ("@i", id))
            + Sql.Scalar<long>(c, "SELECT COUNT(*) FROM products WHERE category=(SELECT name FROM categories WHERE id=@i) AND style_id IS NULL", ("@i", id)));

    /// <summary>Owner-only. Permanently removes a category, only after it was archived and only when no product uses it.</summary>
    public void DeleteCategory(long ownerId, long id)
    {
        using var c = db.Open();
        Db.InOwnerTransaction(c, () =>
        {
            var row = Sql.Query(c, "SELECT name,active FROM categories WHERE id=@i", x => (Name: x.GetString(0), Active: x.GetInt64(1) == 1), ("@i", id)).FirstOrDefault();
            if (row.Name is null) throw new PosException(PosError.NotFound, "Category not found.");
            if (row.Active) throw new PosException(PosError.PermissionDenied, "Archive the category first, then delete it.");
            if (UsageCount(c, id) > 0) throw new PosException(PosError.Duplicate, "Products still use this category, so it can only stay archived.");
            Sql.Exec(c, "DELETE FROM categories WHERE id=@i", ("@i", id));
            Common.Audit(c, clock, ownerId, null, "category.delete", row.Name);
            return 0;
        }, ownerId);
    }
}
