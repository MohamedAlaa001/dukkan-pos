using Dukkan.UI;
using LocalCashier.Application;

namespace LocalCashier.Tests;

public class NavigationRegressionTests
{
    [Fact]
    public void Returning_to_POS_refreshes_catalog_and_customers_without_losing_the_cart()
    {
        Sta.Run(() =>
        {
            using var dir = new TempFolder();
            var s = AppServices.Create(dir.Combine("navigation"), new FakePrinter());
            var owner = s.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
            s.Settings.ConfigureTax(owner, false);
            var product = s.Catalog.AddProduct(owner, "OLD", "OLD", "Original", null, 10);
            s.Inventory.Receive(owner, product, 5, 4);
            var shell = new ShellViewModel(s);
            shell.SignedIn(s.Users.Login("owner", "owner-test-1").User!);
            shell.Nav = "Pos";
            var pos = Assert.IsType<PosViewModel>(shell.Page);
            pos.AddVariant(pos.VariantsOf(pos.Products.Single()).Single());
            shell.Nav = "Products";
            s.Config.AddCategory(owner, "New category");
            s.Catalog.AddProduct(owner, "NEW", "NEW", "New product", null, 20);
            var customer = s.Customers.Add(owner, "New customer");
            shell.Nav = "Pos";
            Assert.Same(pos, shell.Page);
            Assert.Equal(2, pos.Products.Count);
            Assert.Contains("New category", pos.Categories);
            Assert.Contains(pos.Customers, c => c.Id == customer);
            Assert.Single(pos.Cart);
            Assert.Equal(10m, pos.Total);
            return true;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fresh_shop_main_pages_and_empty_state_tabs_open_in_both_languages(bool arabic)
    {
        Sta.Run(() =>
        {
            // The test host has no WPF App startup; initialize its resource URI support.
            _ = System.Windows.Application.GetResourceStream(new Uri("/Dukkan;component/Assets/Icons/basket.png", UriKind.Relative));
            using var dir = new TempFolder();
            var s = AppServices.Create(dir.Combine("fresh"), new FakePrinter());
            var owner = s.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
            s.Config.ApplyPreset(owner, "general", arabic);
            s.Settings.ConfigureTax(owner, false);
            var shell = new ShellViewModel(s);
            shell.SignedIn(s.Users.Login("owner", "owner-test-1").User!);
            shell.SetArabic(arabic);
            try
            {
                foreach (var page in new[] { "Dashboard", "Pos", "Products", "Inventory", "Customers", "Suppliers", "Expenses", "Reports", "Settings" })
                {
                    shell.Nav = page;
                    Assert.NotNull(shell.Page);
                    if (shell.Page is ReportsViewModel reports)
                        for (var tab = 0; tab <= 4; tab++) reports.Tab = tab;
                    if (shell.Page is SettingsViewModel settings)
                        for (var tab = 0; tab <= 8; tab++) settings.Tab = tab;
                    if (shell.Page is StockWorkspaceViewModel stock)
                        for (var tab = 0; tab <= 6; tab++) stock.Tab = tab;
                }
                shell.OpenProductEditor(null);
                Assert.IsType<ProductEditViewModel>(shell.Page).CancelCommand.Execute(null);
                Assert.Empty(s.Styles.List());
                Assert.Empty(s.Config.Categories(true));
            }
            finally { shell.SetArabic(false); }
            return true;
        });
    }
}
