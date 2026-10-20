using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using Xunit;

namespace LocalCashier.Tests;

public class Ean13Tests
{
    [Theory]
    [InlineData("4006381333931")]   // well-known valid EAN-13
    [InlineData("5901234123457")]
    public void Known_codes_validate(string code) => Assert.True(Ean13.IsValid(code));

    [Fact]
    public void Wrong_check_digit_is_rejected() => Assert.False(Ean13.IsValid("4006381333932"));

    [Fact]
    public void Internal_codes_are_valid_unique_and_95_modules()
    {
        var a = Ean13.FromSequence(1);
        var b = Ean13.FromSequence(2);
        Assert.NotEqual(a, b);
        Assert.True(Ean13.IsValid(a));
        Assert.StartsWith("200", a);
        var m = Ean13.Modules(a);
        Assert.Equal(95, m.Length);
        Assert.StartsWith("101", m);
        Assert.EndsWith("101", m);
        Assert.Equal("01010", m.Substring(45, 5));   // centre guard
    }

    [Fact]
    public void Symbol_matches_the_standard_for_a_reference_code()
    {
        // 5901234123457: first digit 5 -> parity LGGLLG
        Assert.Equal("101" + "0001011" + "0100111" + "0110011" + "0010011" + "0111101" + "0011101" + "01010"
            + "1100110" + "1101100" + "1000010" + "1011100" + "1001110" + "1000100" + "101", Ean13.Modules("5901234123457"));
    }
}

public class StyleCatalogTests
{
    static readonly VariantInput[] Variants =
    [
        new("M", "Black", "أسود", "#111827"), new("L", "Black", "أسود", "#111827"), new("L", "White", "أبيض", "#F3F4F6"),
    ];

    static StyleInput Tee => new("T-Shirt", "تيشيرت", "TShirts", "TShirt", 250m, 150m, LowStockAlert: 5);

    [Fact]
    public void Style_creates_variants_with_unique_skus_and_valid_barcodes()
    {
        using var f = new PosFixture();
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var id = styles.CreateStyle(f.Owner, Tee, Variants);
        var v = styles.Variants(id);
        Assert.Equal(3, v.Count);
        Assert.Equal(3, v.Select(x => x.Sku).Distinct().Count());
        Assert.All(v, x => Assert.True(Ean13.IsValid(x.Barcode!)));
        Assert.Equal(3, v.Select(x => x.Barcode).Distinct().Count());
        Assert.All(v, x => Assert.Equal(250m, x.Price));
    }

    [Fact]
    public void Variants_are_sold_with_size_and_color_and_stock_is_per_variant()
    {
        using var f = new PosFixture();
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var id = styles.CreateStyle(f.Owner, Tee, Variants);
        var black = styles.Variants(id)[1];       // L / Black
        f.Inventory.Receive(f.Owner, black.ProductId, 10, 150m);

        var line = new SaleLineRequest(black.ProductId, 2);
        var sale = f.Sales.Post(new PostSaleRequest(Guid.NewGuid(), f.Shift, f.Cashier, null, [line], null, [new PaymentRequest(PaymentMethod.Cash, 500m)]));
        var receipt = f.Sales.GetReceipt(sale.SaleId);
        Assert.Equal("L", receipt.Lines[0].Size);
        Assert.Equal("Black", receipt.Lines[0].Color);
        Assert.Equal(8m, f.Catalog.OnHand(black.ProductId));
        Assert.Equal(0m, f.Catalog.OnHand(styles.Variants(id)[0].ProductId));

        var row = styles.List().Single(s => s.Id == id);
        Assert.Equal(8, row.TotalStock);
        Assert.Equal(3, row.VariantCount);
    }

    [Fact]
    public void Duplicate_variant_is_rejected_and_barcode_scan_finds_the_exact_variant()
    {
        using var f = new PosFixture();
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var id = styles.CreateStyle(f.Owner, Tee, Variants);
        Assert.Equal(PosError.Duplicate, Assert.Throws<PosException>(() => styles.AddVariants(f.Owner, id, [new VariantInput("M", "Black", "أسود", "#111827")])).Code);
        var white = styles.Variants(id)[2];
        var found = f.Catalog.FindByBarcode(white.Barcode!);
        Assert.Equal(white.ProductId, found!.Id);
        Assert.Equal("L", found.Size);
        Assert.Equal("White", found.ColorNameEn);
    }

    [Fact]
    public void Update_follows_to_variants_but_never_rewrites_cost_history_and_archive_blocks_sales()
    {
        using var f = new PosFixture();
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var id = styles.CreateStyle(f.Owner, Tee, Variants);
        var v = styles.Variants(id)[0];
        f.Inventory.Receive(f.Owner, v.ProductId, 5, 150m);

        styles.UpdateStyle(f.Owner, id, Tee with { BasePrice = 300m });
        Assert.All(styles.Variants(id), x => Assert.Equal(300m, x.Price));
        Assert.Equal(150m, styles.Variants(id)[0].AvgCost);

        styles.Archive(f.Owner, id);
        var ex = Assert.Throws<PosException>(() => f.Sales.Post(new PostSaleRequest(Guid.NewGuid(), f.Shift, f.Cashier, null,
            [new SaleLineRequest(v.ProductId, 1)], null, [new PaymentRequest(PaymentMethod.Cash, 300m)])));
        Assert.Equal(PosError.ProductUnavailable, ex.Code);
        Assert.DoesNotContain(styles.List(), s => s.NameEn == "T-Shirt");
    }

    [Fact]
    public void Only_owner_can_manage_styles_and_legacy_products_become_one_variant_styles()
    {
        using var f = new PosFixture();
        var styles = new StyleCatalogService(f.Db, f.Clock);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => styles.CreateStyle(f.Cashier, Tee, Variants)).Code);
        var widget = Assert.Single(styles.List());
        Assert.Equal("Widget", widget.NameEn);
        Assert.Equal(1, widget.VariantCount);
        Assert.Equal(10, widget.TotalStock);
    }
}

public class PrintJobTests
{
    [Fact]
    public void Sale_queues_a_job_and_lifecycle_never_touches_the_sale()
    {
        using var f = new PosFixture();
        var jobs = new PrintJobService(f.Db, f.Clock);
        var sale = f.Sales.Post(f.Sale(1, 10m));
        var job = Assert.Single(jobs.Attention());
        Assert.Equal(PrintStatus.Queued, job.Status);

        jobs.BeginSubmit(job.Id, "HPRT LPQ80");
        Assert.Equal(PrintStatus.Submitting, jobs.Get(job.Id)!.Status);
        jobs.MarkFailed(job.Id, "printer offline");
        var failed = jobs.Get(job.Id)!;
        Assert.Equal(PrintStatus.Failed, failed.Status);
        Assert.Equal("printer offline", failed.Error);
        Assert.Equal(1, f.Count("sales"));
        Assert.Equal(10m, f.Sales.GetReceipt(sale.SaleId).Total);

        jobs.BeginSubmit(job.Id, "HPRT LPQ80");
        jobs.MarkSubmitted(job.Id);
        Assert.Equal(PrintStatus.Submitted, jobs.Get(job.Id)!.Status);
        Assert.Null(jobs.Get(job.Id)!.Error);
        Assert.Empty(jobs.Attention());
    }

    [Fact]
    public void Restart_marks_in_flight_jobs_uncertain_and_reprint_is_a_marked_copy()
    {
        using var f = new PosFixture();
        var jobs = new PrintJobService(f.Db, f.Clock);
        var sale = f.Sales.Post(f.Sale(1, 10m));
        var job = jobs.Attention().Single();
        jobs.BeginSubmit(job.Id, "HPRT LPQ80");

        Assert.Equal(1, jobs.RecoverUncertain());
        Assert.Equal(PrintStatus.Uncertain, jobs.Get(job.Id)!.Status);

        var copyId = jobs.Enqueue(sale.SaleId, "receipt80", isCopy: true);
        Assert.True(jobs.Get(copyId)!.IsCopy);
        Assert.Equal(1, f.Count("sales"));   // a reprint never creates another sale
    }
}
