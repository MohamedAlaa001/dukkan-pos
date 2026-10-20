using Dukkan.UI;
using LocalCashier.Application;

namespace LocalCashier.Tests;

public class InventoryReceivingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Receiving_reviews_the_shared_supplier_invoice_before_posting_stock_and_payment_once(bool paidFromRegister)
    {
        Sta.Run(() =>
        {
            using var folder = new TempFolder();
            var services = AppServices.Create(folder.Combine("invoice-receiving"), new FakePrinter());
            var owner = services.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
            services.Settings.ConfigureTax(owner, true);
            var product = services.Catalog.AddProduct(owner, "W1", "001", "Widget", null, 10m);
            var supplier = services.Suppliers.Create(owner, "Active supplier", null, null, null);
            var archived = services.Suppliers.Create(owner, "Archived supplier", null, null, null);
            services.Suppliers.SetActive(owner, archived, false);
            long? shift = paidFromRegister ? services.Shifts.Open(owner, 500m) : null;
            var shell = new ShellViewModel(services);
            shell.SignedIn(services.Users.Login("owner", "owner-test-1").User!);
            var saved = false;
            var receive = new StockAdjustViewModel(shell, () => saved = true)
                { Query = "001", Qty = "5", Cost = "4.1256", Reference = "SUP-123", PaidFromRegister = paidFromRegister };
            receive.SelectedSupplier = Assert.Single(receive.Suppliers);
            receive.PickCommand.Execute(receive.Results.Single());
            receive.SaveCommand.Execute(null);

            var invoice = Assert.IsType<PurchaseInvoiceViewModel>(shell.Dialog);
            Assert.Equal("Active supplier", invoice.SupplierName);
            Assert.Equal("SUP-123", invoice.SupplierRef);
            Assert.Equal(4.1256m, Assert.Single(invoice.Lines).Cost);
            Assert.Equal(20.63m, invoice.Total);
            Assert.Equal(paidFromRegister ? 20.63m : 0m, invoice.PaidValue);
            Assert.Equal(0m, services.Catalog.OnHand(product));
            Assert.Equal(0m, services.Suppliers.Get(supplier)!.Balance);
            Assert.False(saved);
            if (!paidFromRegister) { invoice.Paid = "5"; invoice.Method = invoice.Methods.Single(m => m.Key == "Pay_Transfer"); }
            invoice.SaveCommand.Execute(null);
            invoice.SaveCommand.Execute(null); // retry cannot receive or pay twice

            Assert.False(shell.HasDialog);
            Assert.True(saved);
            Assert.Equal(5m, services.Catalog.OnHand(product));
            Assert.Equal(paidFromRegister ? 0m : 15.63m, services.Suppliers.Get(supplier)!.Balance);
            Assert.Single(services.Suppliers.InvoiceList(supplier));
            Assert.Single(services.Suppliers.Payments(supplier));
            if (shift is long id) Assert.Equal(479.37m, services.Shifts.ExpectedCash(id));
            return true;
        });
    }

    [Fact]
    public void A_stock_adjustment_still_posts_without_a_supplier_invoice()
    {
        Sta.Run(() =>
        {
            using var folder = new TempFolder();
            var services = AppServices.Create(folder.Combine("adjustment"), new FakePrinter());
            var owner = services.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
            services.Settings.ConfigureTax(owner, true);
            var product = services.Catalog.AddProduct(owner, "W1", "001", "Widget", null, 10m);
            var shell = new ShellViewModel(services);
            shell.SignedIn(services.Users.Login("owner", "owner-test-1").User!);
            var receive = new StockAdjustViewModel(shell, () => { }) { Query = "001", Qty = "3", Cost = "4", IsPurchase = false, Reason = "Opening count" };
            receive.PickCommand.Execute(receive.Results.Single());
            receive.SaveCommand.Execute(null);
            Assert.Equal(3m, services.Catalog.OnHand(product));
            Assert.Empty(services.Suppliers.InvoiceList());
            return true;
        });
    }

    [Fact]
    public void Receiving_without_a_supplier_does_not_post_untracked_stock()
    {
        Sta.Run(() =>
        {
            using var folder = new TempFolder();
            var services = AppServices.Create(folder.Combine("receiving"), new FakePrinter());
            var owner = services.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
            services.Settings.ConfigureTax(owner, true);
            var product = services.Catalog.AddProduct(owner, "W1", "001", "Widget", null, 10m);
            var shell = new ShellViewModel(services);
            shell.SignedIn(services.Users.Login("owner", "owner-test-1").User!);
            var saved = false;
            var receive = new StockAdjustViewModel(shell, () => saved = true) { Query = "001", Qty = "5", Cost = "4" };
            receive.PickCommand.Execute(receive.Results.Single());
            shell.ShowDialog(receive);

            receive.SaveCommand.Execute(null);

            Assert.Equal(0m, services.Catalog.OnHand(product));
            Assert.False(saved);
            Assert.Same(receive, shell.Dialog);
            return true;
        });
    }
}
