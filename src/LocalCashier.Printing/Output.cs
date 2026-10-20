using System.Globalization;
using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Printing;

public static class Rendering
{
    public static BitmapSource ToBitmap(FrameworkElement element, double dpi = 203)
    {
        var w = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
        var h = element.ActualHeight > 0 ? element.ActualHeight : element.Height;
        var scale = dpi / 96d;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), dpi, dpi, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));   // opaque white page
        bmp.Render(dv);
        bmp.Render(element);
        bmp.Freeze();
        return bmp;
    }

    public static byte[] ToPng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// Minimal PDF writer: each page is the rendered visual embedded as a JPEG at full page size. Text is therefore shaped by WPF exactly
/// as on screen (correct Arabic joining and mixed-direction order); the trade-off is that PDF text is not selectable.
/// </summary>
public static class PdfWriter
{
    public static void Save(string path, IReadOnlyList<FrameworkElement> pages, double dpi = 200)
    {
        var jpegs = pages.Select(p =>
        {
            var bmp = Rendering.ToBitmap(p, dpi);
            var enc = new JpegBitmapEncoder { QualityLevel = 92 };
            enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (Data: ms.ToArray(), W: bmp.PixelWidth, H: bmp.PixelHeight,
                    WPt: (p.ActualWidth > 0 ? p.ActualWidth : p.Width) * 72 / 96, HPt: (p.ActualHeight > 0 ? p.ActualHeight : p.Height) * 72 / 96);
        }).ToList();

        File.WriteAllBytes(path, Build(jpegs.Select(j => (j.Data, j.W, j.H, j.WPt, j.HPt)).ToList()));
    }

    public static byte[] Build(IReadOnlyList<(byte[] Jpeg, int W, int H, double WPt, double HPt)> pages)
    {
        using var ms = new MemoryStream();
        var offsets = new List<long>();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void BeginObj(int n) { offsets.Add(ms.Position); Write($"{n} 0 obj\n"); }

        Write("%PDF-1.4\n%âãÏÓ\n");
        // object numbering: 1 catalog, 2 pages, then per page: page, content, image
        BeginObj(1); Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2);
        var kids = string.Join(" ", pages.Select((_, i) => $"{3 + i * 3} 0 R"));
        Write($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var (jpeg, w, h, wPt, hPt) = pages[i];
            var pageObj = 3 + i * 3;
            var wS = wPt.ToString("0.##", CultureInfo.InvariantCulture);
            var hS = hPt.ToString("0.##", CultureInfo.InvariantCulture);
            BeginObj(pageObj);
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {wS} {hS}] /Resources << /XObject << /Im0 {pageObj + 2} 0 R >> >> /Contents {pageObj + 1} 0 R >>\nendobj\n");
            var content = $"q {wS} 0 0 {hS} 0 0 cm /Im0 Do Q";
            BeginObj(pageObj + 1);
            Write($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
            BeginObj(pageObj + 2);
            Write($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            ms.Write(jpeg);
            Write("\nendstream\nendobj\n");
        }

        var xref = ms.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}

/// <summary>Sends visuals to a Windows print queue. Abstracted so job tracking can be tested without hardware.</summary>
public interface IDocumentPrinter
{
    IReadOnlyList<string> Printers();

    /// <summary>Submits the pages to the spooler. Returning normally means "handed to the spooler", not "paper came out".</summary>
    void Print(string printer, IReadOnlyList<FrameworkElement> pages, string jobName);
}

public sealed class WindowsPrinter : IDocumentPrinter
{
    public IReadOnlyList<string> Printers()
    {
        using var server = new LocalPrintServer();
        return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]).Select(q => q.Name).OrderBy(n => n).ToList();
    }

    public void Print(string printer, IReadOnlyList<FrameworkElement> pages, string jobName)
    {
        if (pages.Count == 0) throw new InvalidOperationException("Nothing to print.");
        using var server = new LocalPrintServer();
        PrintQueue queue;
        try { queue = server.GetPrintQueue(printer); }
        catch (Exception ex) { throw new InvalidOperationException($"Printer '{printer}' was not found.", ex); }
        if (queue.IsOffline) throw new InvalidOperationException($"Printer '{printer}' is offline.");

        var dialog = new PrintDialog { PrintQueue = queue };
        var first = pages[0];
        var ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
        // Request the document's own page size (80 mm roll or label); drivers that cannot honour it keep their default.
        ticket.PageMediaSize = new PageMediaSize(first.Width, first.Height);
        try { dialog.PrintTicket = queue.MergeAndValidatePrintTicket(queue.UserPrintTicket ?? queue.DefaultPrintTicket, ticket).ValidatedPrintTicket; }
        catch { /* keep the driver default */ }

        dialog.PrintDocument(new VisualPaginator(pages), jobName);
    }
}

/// <summary>Presents already-laid-out visuals as the pages of one print job.</summary>
public sealed class VisualPaginator(IReadOnlyList<FrameworkElement> pages) : DocumentPaginator
{
    public override bool IsPageCountValid => true;

    public override int PageCount => pages.Count;

    public override Size PageSize { get; set; } = pages.Count > 0 ? new Size(pages[0].Width, pages[0].Height) : new Size(0, 0);

    public override IDocumentPaginatorSource Source => null!;

    public override DocumentPage GetPage(int pageNumber)
    {
        var page = pages[pageNumber];
        var size = new Size(page.Width, double.IsNaN(page.Height) ? page.ActualHeight : page.Height);
        page.Measure(size);
        page.Arrange(new Rect(size));
        return new DocumentPage(page, size, new Rect(size), new Rect(size));
    }
}

/// <summary>Builds the document for a print job, sends it, and records each step so the sale and its print status stay independent.</summary>
public sealed class PrintOrchestrator(SaleService sales, PrintJobService jobs, SettingsService settings, IDocumentPrinter printer)
{
    public StoreInfo Store()
    {
        string V(string k) => settings.GetValue(k) ?? "";
        return new StoreInfo(V("store_name") is { Length: > 0 } n ? n : "Dukkan", V("store_phone"), V("store_address"), V("receipt_footer"), settings.GetValue("receipt_language") == "ar");
    }

    public string? PrinterFor(string kind) => settings.GetValue(kind == "a4" ? "printer_a4" : "printer_receipt");

    public IReadOnlyList<FrameworkElement> BuildPages(PrintJobRow job)
    {
        var receipt = sales.GetReceipt(job.SaleId);
        return job.Kind == "a4" ? DocumentBuilder.InvoiceA4(receipt, Store(), job.IsCopy) : [DocumentBuilder.Receipt80(receipt, Store(), job.IsCopy)];
    }

    /// <summary>Prints one job. Failures are recorded and returned, never thrown into the sale flow.</summary>
    public PrintStatus Print(long jobId)
    {
        var job = jobs.Get(jobId) ?? throw new PosException(PosError.NotFound, "Print job not found.");
        var name = PrinterFor(job.Kind);
        if (string.IsNullOrWhiteSpace(name))
        {
            jobs.MarkFailed(jobId, "No printer is selected in Settings.");
            return PrintStatus.Failed;
        }

        try
        {
            var pages = BuildPages(job);
            jobs.BeginSubmit(jobId, name);
            printer.Print(name, pages, $"Dukkan {job.SaleNumber}");
            jobs.MarkSubmitted(jobId);
            return PrintStatus.Submitted;
        }
        catch (Exception ex)
        {
            jobs.MarkFailed(jobId, ex.Message);
            return PrintStatus.Failed;
        }
    }

    /// <summary>Prints barcode labels: one label per page on a roll (printer_label) or several per A4 sheet (printer_a4). Returns an error text or null.</summary>
    public string? PrintLabels(IReadOnlyList<LabelItem> labels, bool a4Sheet, double widthMm = 50, double heightMm = 30, int skip = 0)
    {
        if (labels.Count == 0) return "No labels to print.";
        var name = a4Sheet ? settings.GetValue("printer_a4") : (settings.GetValue("printer_label") is { Length: > 0 } l ? l : settings.GetValue("printer_receipt"));
        if (string.IsNullOrWhiteSpace(name)) return "No printer is selected in Settings.";
        try
        {
            IReadOnlyList<FrameworkElement> pages = a4Sheet
                ? DocumentBuilder.LabelSheetA4(labels, skip: skip)
                : labels.Select(x => DocumentBuilder.Label(x, widthMm, heightMm)).ToList();
            printer.Print(name, pages, "Dukkan labels");
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Queues a marked copy of an already-posted sale and prints it; never touches the sale itself.</summary>
    public PrintStatus Reprint(long saleId, string kind = "receipt80") => Print(jobs.Enqueue(saleId, kind, isCopy: true));

    public void SavePdf(long saleId, string path, string kind = "a4", bool isCopy = false)
    {
        var receipt = sales.GetReceipt(saleId);
        var pages = kind == "a4" ? DocumentBuilder.InvoiceA4(receipt, Store(), isCopy) : [DocumentBuilder.Receipt80(receipt, Store(), isCopy)];
        PdfWriter.Save(path, pages);
    }
}
