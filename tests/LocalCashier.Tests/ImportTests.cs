using LocalCashier.Domain;
using LocalCashier.Persistence;
using Xunit;

namespace LocalCashier.Tests;

public class ImportTests
{
    const string Products =
        "name,name_ar,category,kind,price,cost,tax_percent,size,color,color_hex,opening_qty,barcode\r\n" +
        "Polo Shirt,قميص بولو,Shirts,Polo,300,150,0,M,Navy,#1B3F73,12,\r\n" +
        "Polo Shirt,قميص بولو,Shirts,Polo,300,150,0,L,Navy,#1B3F73,8,\r\n" +
        "\"Jeans, slim\",جينز,Pants,Jeans,650,300,0,32,Blue,,5,6221234567890\r\n";

    static (PosFixture F, ImportService I) Make()
    {
        var f = new PosFixture();
        return (f, new ImportService(f.Db, f.Clock, new StyleCatalogService(f.Db, f.Clock)));
    }

    [Fact]
    public void Preview_reports_row_specific_errors_without_writing()
    {
        var (f, i) = Make();
        using var _ = f;
        var csv = "name,name_ar,category,kind,price,cost,tax_percent,size,color,color_hex,opening_qty,barcode\r\n" +
                  "A,,Shirts,Polo,abc,10,0,M,Red,,1,\r\n" +          // bad price (row 2)
                  ",,Shirts,Polo,10,5,0,M,Red,,1,\r\n" +             // missing name (row 3)
                  "B,,Shirts,Polo,10,5,0,M,Red,,-4,\r\n" +           // bad qty (row 4)
                  "C,,Shirts,Polo,10,0,0,M,Red,,3,\r\n" +            // stock without cost (row 5)
                  "D,,Shirts,Polo,10,5,0,M,Red,,0,\r\n" +
                  "D,,Shirts,Polo,10,5,0,M,Red,,0,\r\n";             // duplicate in file (row 7)
        var p = i.Preview(ImportKind.Products, csv);
        Assert.False(p.IsValid);
        Assert.Contains(p.Errors, e => e.Row == 2 && e.Column == "price");
        Assert.Contains(p.Errors, e => e.Row == 3 && e.Column == "name");
        Assert.Contains(p.Errors, e => e.Row == 4 && e.Column == "opening_qty");
        Assert.Contains(p.Errors, e => e.Row == 5 && e.Code == "stock_needs_cost");
        Assert.Contains(p.Errors, e => e.Row == 7 && e.Code == "duplicate_file");
        Assert.Equal(0, f.Count("styles") - 1);   // fixture's own back-filled style only
    }

    [Fact]
    public void Missing_columns_and_empty_files_are_explained()
    {
        var (f, i) = Make();
        using var _ = f;
        Assert.Equal("header", i.Preview(ImportKind.Products, "name,cost\r\nA,1\r\n").Errors[0].Code);
        Assert.Equal("empty", i.Preview(ImportKind.Customers, "").Errors[0].Code);
    }

    [Fact]
    public void Apply_creates_styles_variants_and_opening_stock_atomically_and_keeps_arabic()
    {
        var (f, i) = Make();
        using var _ = f;
        var p = i.Preview(ImportKind.Products, Products);
        Assert.True(p.IsValid);
        Assert.Equal((2, 3, 25m), (p.Styles, p.Variants, 20m + 5m));
        var r = i.Apply(f.Owner, ImportKind.Products, Products, "MIG-001");
        Assert.False(r.AlreadyApplied);
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var polo = styles.List().Single(s => s.NameEn == "Polo Shirt");
        Assert.Equal("قميص بولو", polo.NameAlt);
        Assert.Equal(20, polo.TotalStock);
        Assert.Equal(2, polo.VariantCount);
        Assert.Contains(styles.List(), s => s.NameEn == "Jeans, slim" && s.TotalStock == 5);
        Assert.NotNull(f.Catalog.FindByBarcode("6221234567890"));
    }

    [Fact]
    public void One_bad_row_means_nothing_is_imported()
    {
        var (f, i) = Make();
        using var _ = f;
        var bad = Products + "Hat,,Others,Cap,x,1,0,One size,Black,,0,\r\n";
        var before = f.Count("styles");
        Assert.Throws<PosException>(() => i.Apply(f.Owner, ImportKind.Products, bad, "MIG-BAD"));
        Assert.Equal(before, f.Count("styles"));
        Assert.Equal(0, f.Count("import_batches"));
    }

    [Fact]
    public void Retry_with_same_reference_does_not_repeat_opening_stock_and_other_files_are_refused()
    {
        var (f, i) = Make();
        using var _ = f;
        i.Apply(f.Owner, ImportKind.Products, Products, "MIG-001");
        var receipts = f.Count("stock_receipts");
        Assert.True(i.Apply(f.Owner, ImportKind.Products, Products, "MIG-001").AlreadyApplied);
        Assert.Equal(receipts, f.Count("stock_receipts"));
        Assert.Equal(PosError.Duplicate, Assert.Throws<PosException>(() => i.Apply(f.Owner, ImportKind.Products, Products + "\r\n", "MIG-001")).Code);
        // the same names under a new reference are duplicates: imports never overwrite
        Assert.Throws<PosException>(() => i.Apply(f.Owner, ImportKind.Products, Products, "MIG-002"));
    }

    [Fact]
    public void Only_the_owner_can_import_and_a_reference_is_required()
    {
        var (f, i) = Make();
        using var _ = f;
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => i.Apply(f.Cashier, ImportKind.Products, Products, "X")).Code);
        Assert.Equal(PosError.InvalidDiscount, Assert.Throws<PosException>(() => i.Apply(f.Owner, ImportKind.Products, Products, "  ")).Code);
    }

    [Fact]
    public void Closed_onboarding_blocks_opening_stock_and_balances_but_not_plain_catalog()
    {
        var (f, i) = Make();
        using var _ = f;
        i.CloseOnboarding(f.Owner);
        Assert.False(i.IsOnboardingOpen());
        Assert.Contains(i.Preview(ImportKind.Products, Products).Errors, e => e.Code == "onboarding_closed");
        Assert.Contains(i.Preview(ImportKind.Customers, "name,phone,credit_limit,opening_balance\r\nAmal,0100,0,500\r\n").Errors, e => e.Code == "onboarding_closed");
        var plain = "name,name_ar,category,kind,price,cost,tax_percent,size,color,color_hex,opening_qty,barcode\r\nCap,كاب,Others,Cap,100,40,0,One size,Black,,0,\r\n";
        Assert.True(i.Preview(ImportKind.Products, plain).IsValid);
        i.Apply(f.Owner, ImportKind.Products, plain, "CAT-1");
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => i.CloseOnboarding(f.Cashier)).Code);
    }

    [Fact]
    public void Customer_import_creates_opening_balances_without_sales_or_stock()
    {
        var (f, i) = Make();
        using var _ = f;
        var csv = "name,phone,credit_limit,opening_balance\r\nAmal Hassan,01001112222,3000,750.50\r\nNour,,0,0\r\n";
        var stock = f.Catalog.OnHand(f.Widget);
        i.Apply(f.Owner, ImportKind.Customers, csv, "CUST-1");
        var amal = f.Customers.List().Single(c => c.Name == "Amal Hassan");
        Assert.True(amal.CreditEnabled);
        Assert.Equal(750.50m, f.Customers.Balance(amal.Id));
        Assert.Equal(0, f.Count("sales"));
        Assert.Equal(stock, f.Catalog.OnHand(f.Widget));
        Assert.Equal(2, f.Customers.List().Count);
        Assert.Throws<PosException>(() => i.Apply(f.Owner, ImportKind.Customers, csv, "CUST-2"));   // duplicates are not overwritten
    }

    [Fact]
    public void Csv_parser_handles_quotes_newlines_bom_and_template_round_trips()
    {
        var rows = ImportService.ParseCsv("﻿a,\"b,c\",\"d\"\"e\"\r\n\"line1\nline2\",x,\r\n");
        Assert.Equal(["a", "b,c", "d\"e"], rows[0]);
        Assert.Equal("line1\nline2", rows[1][0]);
        var (f, i) = Make();
        using var _ = f;
        Assert.True(i.Preview(ImportKind.Products, ImportService.Template(ImportKind.Products)).IsValid);
        Assert.True(i.Preview(ImportKind.Customers, ImportService.Template(ImportKind.Customers)).IsValid);
    }
}

public class GenericImportTests
{
    [Fact]
    public void Plain_products_with_units_and_new_categories_import_without_any_variant_columns()
    {
        using var f = new PosFixture();
        var i = new ImportService(f.Db, f.Clock, new StyleCatalogService(f.Db, f.Clock));
        var csv = "name,category,unit,price,cost,opening_qty\r\nBasmati rice,Produce,kg,45,30,25.5\r\nBattery AA,Electrical,pc,12,6,100\r\n";
        var p = i.Preview(ImportKind.Products, csv);
        Assert.True(p.IsValid);
        Assert.Equal(2, p.NewCategories);
        i.Apply(f.Owner, ImportKind.Products, csv, "GEN-1");
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var rice = styles.List().Single(s => s.NameEn == "Basmati rice");
        Assert.Equal(25.5m, rice.TotalStock);
        var detail = styles.Get(rice.Id)!;
        Assert.Equal(("kg", true), (detail.Input.Unit, detail.Input.AllowDecimal));
        Assert.Contains(new CatalogConfigService(f.Db, f.Clock).Categories(), c => c.Name == "Electrical");
        Assert.Equal(25.5m, f.Catalog.OnHand(styles.Variants(rice.Id).Single().ProductId));
    }

    [Fact]
    public void Fractions_are_refused_for_unit_pieces_and_unknown_units_are_explained()
    {
        using var f = new PosFixture();
        var i = new ImportService(f.Db, f.Clock, new StyleCatalogService(f.Db, f.Clock));
        var p = i.Preview(ImportKind.Products, "name,unit,price,cost,opening_qty\r\nA,pc,10,5,1.5\r\nB,bag,10,5,1\r\n");
        Assert.Contains(p.Errors, e => e.Row == 2 && e.Code == "invalid_quantity");
        Assert.Contains(p.Errors, e => e.Row == 3 && e.Column == "unit");
    }
}
