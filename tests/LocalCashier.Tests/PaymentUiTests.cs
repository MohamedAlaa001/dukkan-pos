using Dukkan.UI;
using LocalCashier.Application;

namespace LocalCashier.Tests;

public class PaymentUiTests
{
    [Theory]
    [InlineData("purchase")]
    [InlineData("supplier")]
    [InlineData("collection")]
    public void Shared_card_dialogs_require_reconfirmation_when_the_amount_changes(string dialog)
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("manual"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");
            s.Settings.ConfigureTax(owner,false);var product=s.Catalog.AddProduct(owner,"W","W","Widget",null,10);s.Inventory.Receive(owner,product,10,4);var shift=s.Shifts.Open(owner,500);
            var customer=s.Customers.Add(owner,"Customer",creditEnabled:true,creditLimit:100);s.Sales.Post(new(Guid.NewGuid(),shift,owner,customer,[new(product,2)],null,[]));
            var supplier=s.Suppliers.Create(owner,"Supplier");s.Suppliers.AddOpeningBalance(Guid.NewGuid(),owner,supplier,100);
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);var item=new SupplierItem(s.Suppliers.Get(supplier)!);
            ManualCardEntry card;
            if(dialog=="purchase")
            {
                var pos=new PosViewModel(shell);var vm=new PurchaseInvoiceViewModel(shell,item,()=>{});vm.PickCommand.Execute(pos.VariantsOf(pos.Products.Single()).Single());vm.Lines[0].CostText="20";
                vm.Method=vm.Methods.Single(m=>m.Kind=="card");vm.Paid="10";card=vm.Card;card.Confirmed=true;vm.Paid="20";
            }
            else if(dialog=="supplier")
            {
                var vm=new SupplierPayViewModel(shell,item,()=>{});vm.Method=vm.Methods.Single(m=>m.Kind=="card");vm.Amount="10";card=vm.Card;card.Confirmed=true;vm.Amount="20";
            }
            else
            {
                var vm=new CollectPaymentViewModel(shell,new CustomerItem(s.Customers.List().Single()),()=>{});vm.Method=vm.Methods.Single(m=>m.Kind=="card");vm.Amount="10";card=vm.Card;card.Confirmed=true;vm.Amount="20";
            }
            Assert.False(card.Confirmed);return true;
        });
    }
    [Fact]
    public void Single_card_requires_fresh_confirmation_and_receiving_selector_keeps_wallet_identity()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("shared"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");
            s.Settings.ConfigureTax(owner,false);var product=s.Catalog.AddProduct(owner,"W","W","Widget",null,10);s.Inventory.Receive(owner,product,10,4);s.Shifts.Open(owner,500);
            var wallet=s.PaymentMethods.List(false).Single(m=>m.Code=="vodafone_cash");s.PaymentMethods.Update(owner,wallet.Id,wallet.NameEn,wallet.NameAr,true,wallet.SortOrder);
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);var pos=new PosViewModel(shell);var variant=pos.VariantsOf(pos.Products.Single()).Single();pos.AddVariant(variant);
            var card=new PaymentViewModel(shell,pos){Method="Card",ConfirmedOnTerminal=true};Assert.True(card.IsValid);
            card.ElectronicText="9";Assert.False(card.ConfirmedOnTerminal);Assert.False(card.IsValid);
            card.ElectronicText="10";card.ConfirmedOnTerminal=true;card.Last4="1234";card.CompleteCommand.Execute(null);
            var supplier=s.Suppliers.Create(owner,"Supplier");var vm=new PurchaseInvoiceViewModel(shell,new SupplierItem(s.Suppliers.Get(supplier)!),()=>{});
            vm.PickCommand.Execute(variant);vm.Lines[0].QtyText="1";vm.Lines[0].CostText="4";vm.Paid="4";vm.Method=vm.Methods.Single(m=>m.Id==wallet.Id);vm.PaymentReference="W-RECEIPT";vm.SaveCommand.Execute(null);
            Assert.Equal(wallet.Id,s.Suppliers.Payments(supplier).Single().MethodId);Assert.Equal("W-RECEIPT",s.Suppliers.Payments(supplier).Single().Reference);
            Assert.Equal(10m,s.Catalog.OnHand(product));return true;
        });
    }
    [Fact]
    public void Split_dialog_tracks_remaining_cash_change_and_manual_confirmation_and_posts_all_rows()
    {
        Sta.Run(()=>
        {
            using var dir=new TempFolder();var s=AppServices.Create(dir.Combine("payments"),new FakePrinter());var owner=s.Users.Create("owner","Owner",Role.Owner,"owner-test-1");
            s.Settings.ConfigureTax(owner,false);var product=s.Catalog.AddProduct(owner,"W","W","Widget",null,10);s.Inventory.Receive(owner,product,10,4);s.Shifts.Open(owner,500);
            var wallet=s.PaymentMethods.List(false).Single(m=>m.Code=="vodafone_cash");s.PaymentMethods.Update(owner,wallet.Id,wallet.NameEn,wallet.NameAr,true,wallet.SortOrder);
            var shell=new ShellViewModel(s);shell.SignedIn(s.Users.Login("owner","owner-test-1").User!);var pos=new PosViewModel(shell);pos.AddVariant(pos.VariantsOf(pos.Products.Single()).Single());
            var vm=new PaymentViewModel(shell,pos){IsSplit=true};vm.PaymentLines.Clear();vm.AddPaymentCommand.Execute(null);
            vm.PaymentLines[0].Amount="3";vm.AddPaymentCommand.Execute(null);vm.PaymentLines[1].Method=vm.TenderMethods.Single(m=>m.Key=="Card");vm.PaymentLines[1].Amount="4";
            vm.AddPaymentCommand.Execute(null);vm.PaymentLines[2].Method=vm.TenderMethods.Single(m=>m.Id==wallet.Id);vm.PaymentLines[2].Amount="3";vm.PaymentLines[2].Reference="REF-W";
            Assert.Equal(0m,vm.RemainingOnAccount);Assert.False(vm.IsValid);
            vm.PaymentLines[1].ConfirmedOnTerminal=true;vm.PaymentLines[1].Last4="1234";vm.PaymentLines[1].ApprovalCode="OK";Assert.True(vm.IsValid);
            vm.PaymentLines[0].Amount="5";Assert.Equal(2m,vm.Change);vm.CompleteCommand.Execute(null);
            var completed=Assert.IsType<SaleCompleteViewModel>(shell.Dialog);var sale=s.Queries.Invoices(s.Queries.Today,s.Queries.Today).Single();
            Assert.Equal(completed.Number,sale.Number);Assert.Equal(3,s.Sales.GetReceipt(sale.Id).Payments.Count);Assert.Equal(503m,s.Shifts.ExpectedCash(shell.Shift!.Id));
            return true;
        });
    }
}
