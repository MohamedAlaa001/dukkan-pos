using Dukkan.UI;
using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace LocalCashier.Tests;

public class SupplierDocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Count_sheet_is_paged_laid_out_and_blind_to_book_quantities_and_costs(bool arabic)
    {
        Sta.Run(()=>
        {
            var keys=new[]{"Stock_Counts","Stock_Location","Col_Product","Stock_Batch","Stock_Counted","Stock_Expected"};
            var rows=Enumerable.Range(1,25).Select(i=>new StockCountLineRow(i,i,"Item "+i,null,"BATCH",123.456m,null,98.76m)).ToList();
            var pages=StockDocuments.CountSheet(new(1,1,"Main","open","2026-01-01"),rows,arabic,keys.ToDictionary(k=>k,k=>Strings.Get(k,arabic)));
            Assert.Equal(2,pages.Count);
            Assert.All(pages,p=>{Assert.True(p.ActualWidth>790);Assert.True(Rendering.ToBitmap(p,96).PixelHeight>1100);Assert.DoesNotContain("123.456",Sta.AllText(p));Assert.DoesNotContain("98.76",Sta.AllText(p));});
            return true;
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Order_document_is_laid_out_and_renders_ink_before_pdf_export(bool arabic)
    {
        Sta.Run(() =>
        {
            var row = new PurchaseOrderRow(1, 1, "Supplier", "PO1-00001", "open", "2026-10-20", "Notes", 30, 3, 0, false);
            var keys = new[] { "PO_Title", "Col_Supplier", "PO_Expected", "Col_Item", "Col_Qty", "Col_Cost", "Col_Total", "PO_NoPosting" };
            var pages = SupplierDocuments.PurchaseOrder(row, [new(1, 1, "Item", 3, 10, 0)], "Dukkan", arabic,
                keys.ToDictionary(k => k, k => Strings.Get(k, arabic)));
            var bitmap = Rendering.ToBitmap(Assert.Single(pages), 96);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var dark = 0;
            for (var y = 3; y < bitmap.PixelHeight - 3; y++)
            for (var x = 3; x < bitmap.PixelWidth - 3; x++)
            {
                var i = (y * bitmap.PixelWidth + x) * 4;
                if (pixels[i + 3] == 255 && pixels[i] < 180 && pixels[i + 1] < 180 && pixels[i + 2] < 180) dark++;
            }
            Assert.True(dark > 500, "PO must contain visible text before PdfWriter renders it; an unarranged WPF page exports blank.");
            return 0;
        });
    }

    [Fact]
    public void Invoice_selection_uses_document_number_in_custom_combo_template()
    {
        var item = new SupplierInvoiceItem(new SupplierInvoiceRow(1, 1, "Supplier", "P1-000004", "purchase", null, "2026-10-19", null, 10, 0, 10, "unpaid", false, false));
        Assert.Equal(item.Number, item.ToString());
    }
}
