using Dukkan.UI;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class PromotionUiTests
{
    [Fact] public void Editing_an_offer_preserves_its_archived_product_scope()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("archive"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");s.Settings.ConfigureTax(owner,false);
            var product=s.Catalog.AddProduct(owner,"W","W","Widget",null,10);var service=new PromotionService(s.Db,s.Clock);
            var id=service.Save(Guid.NewGuid(),owner,new(0,"Offer","عرض",PromotionKind.Percentage){Percent=10,ProductIds=[product]});s.Styles.Archive(owner,s.Catalog.GetById(product)!.StyleId);
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);var editor=new PromotionEditViewModel(shell,service.List().Single(),()=>{});editor.NameEn="Renamed";editor.SaveCommand.Execute(null);
            Assert.Equal("",editor.Error);Assert.Equal(new[]{product},service.List().Single(r=>r.Id==id).ProductIds);return true;
        });
    }
    [Fact] public void Editor_does_not_save_an_ignored_minimum_for_percentage_rules()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("minimum"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);
            var editor=new PromotionEditViewModel(shell,null,()=>{}){NameEn="Offer",NameAr="عرض",Minimum="100"};editor.SaveCommand.Execute(null);
            Assert.Equal("",editor.Error);Assert.Equal(0,new PromotionService(s.Db,s.Clock).List().Single().MinimumCents);return true;
        });
    }
    [Fact] public void Owner_editor_saves_bundle_scope_and_loyalty_configuration()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("edit"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");s.Settings.ConfigureTax(owner,false);
            var product=s.Catalog.AddProduct(owner,"W","W","Widget",null,10);var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);
            var editor=new PromotionEditViewModel(shell,null,()=>{});editor.NameEn="Bundle";editor.NameAr="باقة";editor.Kind=editor.Kinds.Single(k=>k.Kind==PromotionKind.Bundle);editor.Amount="15";
            editor.Products.Single(p=>p.Id==product).Selected=true;editor.Products.Single(p=>p.Id==product).Quantity="2";editor.SaveCommand.Execute(null);Assert.Equal("",editor.Error);
            var rule=new PromotionService(s.Db,s.Clock).List().Single();Assert.Equal(1500,rule.AmountCents);Assert.Equal(2,rule.Bundle.Single().Quantity);
            var workspace=new PromotionWorkspaceViewModel(shell){Enabled=true,EarnRate="1.25",PointValue="0.10",Minimum="5",ExpiryDays="365"};workspace.SaveConfigCommand.Execute(null);
            Assert.Equal("",workspace.Error);Assert.Equal(new(true,1.25m,10,5,365),new LoyaltyService(s.Db,s.Clock).Settings());return true;
        });
    }
    [Fact] public void Cart_automatic_offer_preview_and_post_share_the_exact_price()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("promo"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");s.Settings.ConfigureTax(owner,false);
            var product=s.Catalog.AddProduct(owner,"W","W","Widget",null,10);s.Inventory.Receive(owner,product,10,4);s.Shifts.Open(owner,500);
            new PromotionService(s.Db,s.Clock).Save(Guid.NewGuid(),owner,new(0,"Offer","عرض",PromotionKind.Percentage){Percent=10,Coupon="SAVE"});
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);var pos=new PosViewModel(shell);pos.AddVariant(pos.VariantsOf(pos.Products.Single()).Single());
            Assert.Equal(10m,pos.Total);pos.Coupon="SAVE";Assert.Equal(9m,pos.Total);Assert.Equal(1m,pos.PromotionDiscount);Assert.Single(pos.AppliedOffers);
            var payment=new PaymentViewModel(shell,pos);payment.CompleteCommand.Execute(null);Assert.IsType<SaleCompleteViewModel>(shell.Dialog);
            Assert.Equal(9m,s.Sales.GetReceipt(s.Queries.Invoices(s.Queries.Today,s.Queries.Today).Single().Id).Total);return true;
        });
    }
    [Fact] public void Payment_redemption_changes_payable_and_posts_points_as_an_original_tender()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("loyalty"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");s.Settings.ConfigureTax(owner,false);
            var product=s.Catalog.AddProduct(owner,"W","W","Widget",null,20);s.Inventory.Receive(owner,product,10,4);s.Shifts.Open(owner,500);var customer=s.Customers.Add(owner,"Member");
            var loyalty=new LoyaltyService(s.Db,s.Clock);loyalty.Configure(owner,new(true,1,10,1,0));loyalty.Adjust(Guid.NewGuid(),owner,customer,100,"Opening");
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);var pos=new PosViewModel(shell);pos.AddVariant(pos.VariantsOf(pos.Products.Single()).Single());pos.SelectedCustomer=pos.Customers.Single(c=>c.Id==customer);
            var payment=new PaymentViewModel(shell,pos);payment.RedeemPointsText="50";Assert.Equal(15m,payment.Total);Assert.Equal("15",payment.CashText);
            payment.CompleteCommand.Execute(null);Assert.IsType<SaleCompleteViewModel>(shell.Dialog);Assert.Equal(65,loyalty.Balance(customer));return true;
        });
    }
}
