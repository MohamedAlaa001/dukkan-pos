using LocalCashier.Domain;
using LocalCashier.Persistence;
using Xunit;

namespace LocalCashier.Tests;

public class CatalogConfigTests
{
    [Fact]
    public void A_shop_that_never_chose_a_type_keeps_working_as_a_clothing_shop()
    {
        using var f = new PosFixture();
        var cfg = new CatalogConfigService(f.Db, f.Clock).Get();
        Assert.Equal(("clothing", "Size", "Color", true), (cfg.BusinessType, cfg.Option1Name, cfg.Option2Name, cfg.Option2IsColor));
    }

    [Fact]
    public void Presets_set_option_names_only_and_stay_editable()
    {
        using var f = new PosFixture();
        var svc = new CatalogConfigService(f.Db, f.Clock);
        svc.ApplyPreset(f.Owner, "grocery", arabic: false);
        var cfg = svc.Get();
        Assert.Equal("Pack size", cfg.Option1Name);
        Assert.False(cfg.HasOption2);
        Assert.Empty(svc.Categories(true));                 // no predefined categories, ever

        svc.ApplyPreset(f.Owner, "general", arabic: true);
        Assert.False(svc.Get().HasOption1);
        svc.Save(f.Owner, svc.Get() with { Option1Name = "Weight", Option1Presets = ["1 kg", "2 kg"] });
        Assert.Equal(["1 kg", "2 kg"], svc.Get().Option1Presets);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => svc.ApplyPreset(f.Cashier, "clothing", false)).Code);
    }

    [Fact]
    public void Categories_can_be_added_renamed_and_archived_and_renaming_updates_products()
    {
        using var f = new PosFixture();
        var svc = new CatalogConfigService(f.Db, f.Clock);
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var id = svc.AddCategory(f.Owner, "Gadgets");
        Assert.Throws<PosException>(() => svc.AddCategory(f.Owner, "gadgets"));
        var style = styles.CreateStyle(f.Owner, new StyleInput("Lamp", null, "Gadgets", "TShirt", 50m, 20m), [new VariantInput("", "", "", "")]);
        svc.RenameCategory(f.Owner, id, "Lighting", "إضاءة");
        Assert.Equal("Lighting", styles.Get(style)!.Input.CategoryKey);
        svc.SetCategoryActive(f.Owner, id, false);
        Assert.DoesNotContain(svc.Categories(), c => c.Name == "Lighting");
        Assert.Contains(svc.Categories(true), c => c.Name == "Lighting");
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => svc.AddCategory(f.Cashier, "X")).Code);
    }

    [Fact]
    public void Products_without_variants_and_weight_units_work_through_a_sale()
    {
        using var f = new PosFixture();
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var id = styles.CreateStyle(f.Owner, new StyleInput("Rice", "أرز", "Others", "TShirt", 30m, 20m, Unit: "kg", AllowDecimal: true), [new VariantInput("", "", "", "")]);
        var v = styles.Variants(id).Single();
        Assert.Matches(@"^[A-Z]{2}\d{3}$", v.Sku);              // no size/color suffix
        f.Inventory.Receive(f.Owner, v.ProductId, 10m, 20m);
        f.Sales.Post(new LocalCashier.Application.PostSaleRequest(Guid.NewGuid(), f.Shift, f.Cashier, null,
            [new LocalCashier.Application.SaleLineRequest(v.ProductId, 2.5m)], null, [new LocalCashier.Application.PaymentRequest(LocalCashier.Application.PaymentMethod.Cash, 100m)]));
        Assert.Equal(7.5m, f.Catalog.OnHand(v.ProductId));
        Assert.Equal(("kg", true), (styles.Get(id)!.Input.Unit, styles.Get(id)!.Input.AllowDecimal));
    }
}

public class CategoryPictureTests
{
    static string IconsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Dukkan.UI"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Dukkan.UI", "Assets", "Icons");
    }

    [Fact]
    public void Every_preset_category_uses_a_bundled_picture_and_every_name_has_a_file()
    {
        foreach (var name in BusinessPresets.IconNames) Assert.True(File.Exists(Path.Combine(IconsDir(), name + ".png")), $"missing {name}.png");
        foreach (var p in BusinessPresets.All)
            foreach (var c in p.Categories) Assert.Contains(c.Icon, BusinessPresets.IconNames);
        Assert.Equal(BusinessPresets.IconNames.Count, BusinessPresets.IconNames.Distinct().Count());
        Assert.True(BusinessPresets.All.Count >= 8);
    }

    [Fact]
    public void Categories_keep_their_picture_through_presets_edits_and_old_data()
    {
        using var f = new PosFixture();
        var svc = new CatalogConfigService(f.Db, f.Clock);
        var id = svc.AddCategory(f.Owner, "Gadgets", null, "phone");
        svc.RenameCategory(f.Owner, id, "Gadgets", null, "camera");
        Assert.Equal("camera", svc.Categories().Single(c => c.Id == id).Icon);
        svc.RenameCategory(f.Owner, id, "Gadgets", null, "");
        Assert.Null(svc.Categories().Single(c => c.Id == id).Icon);
        Assert.Equal(PosError.InvalidPrice, Assert.Throws<PosException>(() => svc.AddCategory(f.Owner, "Bad", null, "not-a-picture")).Code);
        svc.ApplyPreset(f.Owner, "grocery", false);                       // applying a shop type never touches categories
        Assert.Single(svc.Categories());
    }

    [Fact]
    public void A_category_must_be_archived_first_and_can_be_deleted_only_when_unused()
    {
        using var f = new PosFixture();
        var svc = new CatalogConfigService(f.Db, f.Clock);
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var used = svc.AddCategory(f.Owner, "Used");
        var spare = svc.AddCategory(f.Owner, "Spare");
        styles.CreateStyle(f.Owner, new StyleInput("Lamp", null, "Used", "TShirt", 50m, 20m), [new VariantInput("", "", "", "")]);

        Assert.Throws<PosException>(() => svc.DeleteCategory(f.Owner, spare));                       // still active
        svc.SetCategoryActive(f.Owner, spare, false);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => svc.DeleteCategory(f.Cashier, spare)).Code);
        svc.DeleteCategory(f.Owner, spare);
        Assert.DoesNotContain(svc.Categories(true), c => c.Id == spare);

        svc.SetCategoryActive(f.Owner, used, false);
        Assert.Equal(1, svc.CategoryUsage(used));
        Assert.Throws<PosException>(() => svc.DeleteCategory(f.Owner, used));                        // a product still uses it
        Assert.Contains(svc.Categories(true), c => c.Id == used);
    }
}
