using Dukkan.UI;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Tests;

public class CheckoutCaptureUiTests
{
    [Fact]
    public void Captured_member_requotes_before_posting_and_can_complete_after_review()
    {
        Sta.Run(() =>
        {
            using var dir = new TempFolder();
            var s = AppServices.Create(dir.Combine("member"), new FakePrinter());
            var owner = s.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
            s.Settings.ConfigureTax(owner, false);
            var product = s.Catalog.AddProduct(owner, "W", "W", "Widget", null, 10);
            s.Inventory.Receive(owner, product, 5, 4);
            s.Shifts.Open(owner, 100);
            var customer = s.Customers.Add(owner, "Member", "01012345678");
            var promotions = new PromotionService(s.Db, s.Clock);
            promotions.SetCustomerGroup(Guid.NewGuid(), owner, customer, "VIP");
            promotions.Save(Guid.NewGuid(), owner, new(0, "Member offer", "عرض الأعضاء", PromotionKind.Percentage) { Percent = 10, CustomerGroup = "VIP" });
            var shell = new ShellViewModel(s);
            shell.SignedIn(s.Users.Login("owner", "owner-test-1").User!);
            var pos = new PosViewModel(shell);
            pos.AddVariant(pos.VariantsOf(pos.Products.Single()).Single());
            var payment = new PaymentViewModel(shell, pos) { CustomerPhone = "01012345678" };
            Assert.True(payment.HasMatch);
            Assert.Equal(10m, payment.Total);
            payment.CompleteCommand.Execute(null);
            Assert.Empty(s.Queries.Invoices(s.Queries.Today, s.Queries.Today));
            Assert.Equal(9m, payment.Total);
            Assert.True(payment.HasError);
            Assert.Equal(customer, pos.SelectedCustomer.Id);
            Assert.Contains(payment.SelectedCustomer, payment.Customers);
            Assert.Equal("9", payment.CashText);
            payment.CompleteCommand.Execute(null);
            Assert.IsType<SaleCompleteViewModel>(shell.Dialog);
            var invoice = Assert.Single(s.Queries.Invoices(s.Queries.Today, s.Queries.Today));
            Assert.Equal(9m, s.Sales.GetReceipt(invoice.Id).Total);
            Assert.Single(s.Customers.List());
            return true;
        });
    }

    [Fact]
    public void Successful_capture_notifies_the_retained_POS_customer_selector()
    {
        Sta.Run(() =>
        {
            using var dir = new TempFolder();
            var s = AppServices.Create(dir.Combine("capture"), new FakePrinter());
            var owner = s.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
            s.Settings.ConfigureTax(owner, false);
            var product = s.Catalog.AddProduct(owner, "W", "W", "Widget", null, 10);
            s.Inventory.Receive(owner, product, 5, 4);
            s.Shifts.Open(owner, 100);
            var shell = new ShellViewModel(s);
            shell.SignedIn(s.Users.Login("owner", "owner-test-1").User!);
            var pos = new PosViewModel(shell);
            pos.AddVariant(pos.VariantsOf(pos.Products.Single()).Single());
            var customerChanges = 0;
            pos.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PosViewModel.Customers)) customerChanges++; };
            var payment = new PaymentViewModel(shell, pos) { CustomerName = "New customer", CustomerPhone = "01012345678" };
            payment.CompleteCommand.Execute(null);
            Assert.IsType<SaleCompleteViewModel>(shell.Dialog);
            Assert.True(customerChanges > 0);
            Assert.Contains(pos.Customers, c => c.Name == "New customer");
            Assert.Equal(0, pos.SelectedCustomer.Id);
            Assert.Contains(payment.SelectedCustomer, payment.Customers);
            return true;
        });
    }

    [Fact]
    public void Clearing_the_checkout_customer_selector_returns_to_walk_in_safely()
    {
        Sta.Run(() =>
        {
            using var dir = new TempFolder();
            var s = AppServices.Create(dir.Combine("selection"), new FakePrinter());
            s.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
            var shell = new ShellViewModel(s);
            shell.SignedIn(s.Users.Login("owner", "owner-test-1").User!);
            var payment = new PaymentViewModel(shell, new PosViewModel(shell));
            payment.SelectedCustomer = null!;
            Assert.Equal(0, payment.SelectedCustomer.Id);
            return true;
        });
    }
}
