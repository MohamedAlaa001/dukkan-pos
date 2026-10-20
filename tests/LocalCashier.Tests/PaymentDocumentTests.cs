using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace LocalCashier.Tests;

public class PaymentDocumentTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Receipts_include_saved_offer_reasons_and_points(bool arabic)
    {
        Sta.Run(()=>
        {
            var receipt=new ReceiptModel("S-POINTS","2026-01-01T10:00:00+03:00","Cashier",[new("Widget",1,10,1,9)],1,0,9,[new("cash",9,9)],0,0,
                [new("Saved offer","عرض محفوظ",1)],5,95,10,true);
            foreach(var text in new[]{Sta.AllText(DocumentBuilder.Receipt80(receipt,new("Dukkan",null,null,null,arabic),false)),string.Join(" ",DocumentBuilder.InvoiceA4(receipt,new("Dukkan",null,null,null,arabic),false).Select(Sta.AllText))})
            {Assert.Contains(arabic?"عرض محفوظ":"Saved offer",text);Assert.Contains("95",text);Assert.Contains(arabic?"نقاط مكتسبة":"Points earned",text);}
            return true;
        });
    }
    [Fact]
    public void Many_tenders_and_wrapped_references_stay_inside_A4_printable_body()
    {
        Sta.Run(()=>
        {
            var receipt=new ReceiptModel("S-LONG","2026-01-01T10:00:00+03:00","Cashier",
                Enumerable.Range(1,26).Select(i=>new ReceiptLine("Product "+i,1,10,0,10)).ToArray(),0,0,260,
                Enumerable.Range(1,12).Select(i=>new ReceiptPayment("transfer",20,20,"wallet","Wallet "+i,"محفظة",new string('R',100))).ToArray(),0,20);
            var pages=DocumentBuilder.InvoiceA4(receipt,new("Dukkan",null,null,null,false),false);
            foreach(System.Windows.Controls.Grid page in pages)
            {
                var body=(System.Windows.Controls.StackPanel)page.Children[0];
                body.Measure(new System.Windows.Size(page.Width-body.Margin.Left-body.Margin.Right,double.PositiveInfinity));
                Assert.True(body.DesiredSize.Height<=page.Height+0.1,$"Body height {body.DesiredSize.Height} exceeds page {page.Height}; pages={pages.Count}; child heights={string.Join(",",body.Children.Cast<System.Windows.UIElement>().Select(e=>e.DesiredSize.Height))}");
            }
            Assert.Contains("Wallet 12",string.Join(" ",pages.Select(Sta.AllText)));
            return true;
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Thermal_and_A4_use_saved_wallet_names_and_manual_card_details(bool arabic)
    {
        Sta.Run(()=>
        {
            var receipt=new ReceiptModel("S-TEST","2026-01-01T10:00:00+03:00","Cashier",[new("Widget",1,10,0,10)],0,0,10,
                [new("card",4,4,"card","Saved card","بطاقة محفوظة",null,"APP-1","1234"),new("transfer",6,6,"vodafone_cash","Saved wallet","محفظة محفوظة","REF-W")],0,0);
            var store=new StoreInfo("Dukkan",null,null,null,arabic);
            var thermal=Sta.AllText(DocumentBuilder.Receipt80(receipt,store,false));var a4=string.Join(" ",DocumentBuilder.InvoiceA4(receipt,store,false).Select(Sta.AllText));
            foreach(var text in new[]{thermal,a4})
            {
                Assert.Contains(arabic?"محفظة محفوظة":"Saved wallet",text);Assert.Contains("REF-W",text);Assert.Contains("APP-1",text);Assert.Contains("****1234",text);Assert.DoesNotContain("Instapay",text);
            }
            return true;
        });
    }
}
