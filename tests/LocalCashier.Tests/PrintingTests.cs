using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using LocalCashier.Printing;
using Xunit;

namespace LocalCashier.Tests;

static class Sta
{
    public static T Run<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("STA failure: " + error.Message, error);
        return result;
    }

    public static string AllText(DependencyObject root)
    {
        var sb = new StringBuilder();
        void Walk(DependencyObject d)
        {
            if (d is TextBlock tb) sb.AppendLine(tb.Text);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }

        Walk(root);
        return sb.ToString();
    }
}

sealed class FakePrinter(Action<string>? onPrint = null) : IDocumentPrinter
{
    public List<(string Printer, int Pages, string Job)> Printed { get; } = [];

    public IReadOnlyList<string> Printers() => ["HPRT LPQ80", "A4 Laser"];

    public void Print(string printer, IReadOnlyList<FrameworkElement> pages, string jobName)
    {
        onPrint?.Invoke(printer);
        Printed.Add((printer, pages.Count, jobName));
    }
}

public class PrintingTests
{
    static ReceiptModel SampleReceipt(bool credit = false) => new(
        "S1-000042", "2024-08-12T14:35:00+03:00", "Mona Samir",
        [new ReceiptLine("T-Shirt", 2, 250m, 0m, 500m, "L", "Black"), new ReceiptLine("قميص بولو", 1, 300m, 30m, 270m, "M", "كريمي")],
        30m, 0m, 770m, [new ReceiptPayment("cash", credit ? 500m : 770m, 800m)], credit ? 0m : 30m, credit ? 270m : 0m);

    static readonly StoreInfo En = new("Dukkan Clothing Shop", "01012345678", "12 Tahrir St, Cairo", "Exchange within 14 days", false);
    static readonly StoreInfo Ar = new("محل كابوكي للملابس", "01012345678", "12 شارع التحرير، القاهرة", null, true);

    [Fact]
    public void Receipt_is_72mm_wide_with_lines_variants_totals_and_change()
    {
        var text = Sta.Run(() =>
        {
            var e = DocumentBuilder.Receipt80(SampleReceipt(), En, isCopy: false);
            Assert.Equal(72 * DocumentBuilder.MmToDip, e.Width, 1);
            Assert.True(e.ActualHeight > 250);
            return Sta.AllText(e);
        });
        Assert.Contains("Dukkan Clothing Shop", text);
        Assert.Contains("S1-000042", text);
        Assert.Contains("L / Black", text);
        Assert.Contains("770.00", text);
        Assert.Contains("30.00", text);
        Assert.Contains("Exchange within 14 days", text);
        Assert.DoesNotContain("COPY", text);
    }

    [Fact]
    public void Arabic_receipt_is_right_to_left_and_copy_is_marked()
    {
        var (text, flow) = Sta.Run(() =>
        {
            var e = DocumentBuilder.Receipt80(SampleReceipt(credit: true), Ar, isCopy: true);
            return (Sta.AllText(e), e.FlowDirection);
        });
        Assert.Equal(FlowDirection.RightToLeft, flow);
        Assert.Contains("نسخة", text);
        Assert.Contains("المتبقي على الحساب", text);
        Assert.Contains("ج.م", text);
        Assert.Contains("قميص بولو", text);
    }

    [Fact]
    public void A4_invoice_paginates_long_sales()
    {
        var lines = Enumerable.Range(1, 60).Select(i => new ReceiptLine($"Item {i}", 1, 10m, 0m, 10m)).ToList();
        var big = SampleReceipt() with { Lines = lines, Total = 600m, Discount = 0m, Payments = [new ReceiptPayment("card", 600m, 600m)], Change = 0m };
        var pages = Sta.Run(() => DocumentBuilder.InvoiceA4(big, En, false).Select(p => (p.Width, p.Height, Text: Sta.AllText(p))).ToList());
        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => { Assert.Equal(210 * DocumentBuilder.MmToDip, p.Width, 1); Assert.Equal(297 * DocumentBuilder.MmToDip, p.Height, 1); });
        Assert.Contains("Page 3 / 3", pages[2].Text);
        Assert.Contains("600.00", pages[2].Text);
        Assert.DoesNotContain("600.00", pages[0].Text);
    }

    [Fact]
    public void Pdf_output_is_structurally_valid_with_correct_xref_offsets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dukkan-{Guid.NewGuid():N}.pdf");
        try
        {
            Sta.Run(() => { PdfWriter.Save(path, DocumentBuilder.InvoiceA4(SampleReceipt(), En, false)); return 0; });
            var s = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            Assert.StartsWith("%PDF-1.4", s);
            Assert.EndsWith("%%EOF\n", s);
            var start = int.Parse(s[(s.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
            Assert.StartsWith("xref", s[start..]);
            var entries = s[start..].Split('\n').Skip(3).TakeWhile(l => l.EndsWith(" n ")).ToList();
            Assert.Equal(5, entries.Count);   // catalog, pages, page, content, image
            for (var i = 0; i < entries.Count; i++)
                Assert.StartsWith($"{i + 1} 0 obj", s[int.Parse(entries[i][..10])..]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Ean13_label_bars_match_the_standard_pattern_when_rendered()
    {
        var code = Ean13.FromSequence(7);
        var ok = Sta.Run(() =>
        {
            var label = DocumentBuilder.Label(new LabelItem("T-Shirt", "L", "Black", 250m, code, "TS001-BLK-L", "EGP"), 50, 30);
            var bmp = Rendering.ToBitmap(label, 384);
            var width = bmp.PixelWidth;
            var stride = width * 4;
            var pixels = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(pixels, stride, 0);
            bool Dark(int x, int y) => pixels[y * stride + x * 4] < 128;
            var bestY = Enumerable.Range(bmp.PixelHeight / 3, bmp.PixelHeight / 3).MaxBy(y => Enumerable.Range(0, width).Count(x => Dark(x, y)));
            var runs = new List<(bool Dark, int Len)>();
            for (var x = 0; x < width; x++)
            {
                var d = Dark(x, bestY);
                if (runs.Count > 0 && runs[^1].Dark == d) runs[^1] = (d, runs[^1].Len + 1); else runs.Add((d, 1));
            }

            runs = runs.SkipWhile(r => !r.Dark).ToList();
            var expected = new List<int>();
            var m = Ean13.Modules(code);
            for (var i = 0; i < m.Length;)
            {
                var j = i;
                while (j + 1 < m.Length && m[j + 1] == m[i]) j++;
                expected.Add(j - i + 1);
                i = j + 1;
            }

            var take = runs.Take(expected.Count).Select(r => r.Len).ToList();
            var unit = take.Zip(expected, (a, e) => (double)a / e).Average();
            return take.Zip(expected, (a, e) => Math.Abs(a / unit - e) < 0.6).All(x => x);
        });
        Assert.True(ok);
    }

    [Fact]
    public void Label_sheet_fills_a4_pages_and_honours_skipped_positions()
    {
        var item = new LabelItem("Polo", "M", "Navy", 300m, Ean13.FromSequence(3), "TS002", "EGP");
        var pages = Sta.Run(() => DocumentBuilder.LabelSheetA4(Enumerable.Repeat(item, 30).ToList(), 3, 8, skip: 4).Count);
        Assert.Equal(2, pages);
    }

    [Fact]
    public void Orchestrator_records_submitted_failed_and_missing_printer_without_touching_the_sale()
    {
        using var f = new PosFixture();
        var jobs = new PrintJobService(f.Db, f.Clock);
        f.Settings.SetValue(f.Owner, "store_name", "Dukkan Clothing Shop");
        var fake = new FakePrinter();
        var orch = new PrintOrchestrator(f.Sales, jobs, f.Settings, fake);
        var sale = f.Sales.Post(f.Sale(1, 10m));
        var job = jobs.Attention().Single();

        Assert.Equal(PrintStatus.Failed, Sta.Run(() => orch.Print(job.Id)));
        Assert.Contains("No printer", jobs.Get(job.Id)!.Error);

        f.Settings.SetValue(f.Owner, "printer_receipt", "HPRT LPQ80");
        Assert.Equal(PrintStatus.Submitted, Sta.Run(() => orch.Print(job.Id)));
        Assert.Equal("HPRT LPQ80", fake.Printed.Single().Printer);
        Assert.Equal(PrintStatus.Submitted, jobs.Get(job.Id)!.Status);

        var broken = new PrintOrchestrator(f.Sales, jobs, f.Settings, new FakePrinter(p => throw new InvalidOperationException("Printer 'HPRT LPQ80' is offline.")));
        Assert.Equal(PrintStatus.Failed, Sta.Run(() => broken.Reprint(sale.SaleId)));
        var copy = jobs.ForSale(sale.SaleId).Last();
        Assert.True(copy.IsCopy);
        Assert.Contains("offline", copy.Error);
        Assert.Equal(1, f.Count("sales"));
        Assert.Equal(10m, f.Sales.GetReceipt(sale.SaleId).Total);
    }
}

public class PrintSampleDump
{
    [Fact]
    public void Writes_sample_images_when_requested()
    {
        var dir = Environment.GetEnvironmentVariable("DUKKAN_SAMPLES");
        if (string.IsNullOrEmpty(dir)) return;   // design-review helper only
        Directory.CreateDirectory(dir);
        var receipt = new ReceiptModel("S1-000042", "2024-08-12T14:35:00+03:00", "Mona Samir",
            [new ReceiptLine("T-Shirt", 2, 250m, 0m, 500m, "L", "Black"), new ReceiptLine("قميص بولو", 1, 300m, 30m, 270m, "M", "كريمي"), new ReceiptLine("Jeans", 1, 650m, 0m, 650m, "32", "Blue")],
            30m, 0m, 1420m, [new ReceiptPayment("cash", 1420m, 1500m)], 80m, 0m);
        var en = new StoreInfo("Dukkan Clothing Shop", "01012345678", "12 Tahrir St, Cairo", "Exchange within 14 days", false);
        var ar = new StoreInfo("محل كابوكي للملابس", "01012345678", "12 شارع التحرير، القاهرة", "الاستبدال خلال 14 يومًا", true);
        Sta.Run(() =>
        {
            void Save(string name, FrameworkElement e) => File.WriteAllBytes(Path.Combine(dir, name), Rendering.ToPng(Rendering.ToBitmap(e, 203)));
            Save("receipt_en.png", DocumentBuilder.Receipt80(receipt, en, false));
            Save("receipt_ar_copy.png", DocumentBuilder.Receipt80(receipt, ar, true));
            Save("invoice_a4_en.png", DocumentBuilder.InvoiceA4(receipt, en, false)[0]);
            Save("invoice_a4_ar.png", DocumentBuilder.InvoiceA4(receipt, ar, false)[0]);
            Save("label_50x30.png", DocumentBuilder.Label(new LabelItem("T-Shirt", "L", "Black", 250m, Ean13.FromSequence(12), "TS001-BLK-L", "EGP"), 50, 30));
            Save("label_sheet.png", DocumentBuilder.LabelSheetA4(Enumerable.Repeat(new LabelItem("Polo Shirt", "M", "Navy", 300m, Ean13.FromSequence(3), "TS002-NAV-M", "EGP"), 24).ToList())[0]);
            return 0;
        });
    }
}
