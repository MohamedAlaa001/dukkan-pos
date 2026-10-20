using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Dukkan.UI;
using LocalCashier.Application;

namespace LocalCashier.Tests;

public class SupplierFilterTests
{
    [Fact]
    public void Opening_a_supplier_with_the_invoice_filter_bound_does_not_crash_or_lose_selection()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var folder = new TempFolder();
                var services = AppServices.Create(folder.Combine("supplier-filter"), new FakePrinter());
                var owner = services.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
                services.Settings.ConfigureTax(owner, true);
                var id = services.Suppliers.Create(owner, "Supplier", null, null, null);
                var shell = new ShellViewModel(services);
                shell.SignedIn(services.Users.Login("owner", "owner-test-1").User!);
                shell.Nav = "Suppliers";
                var page = Assert.IsType<SuppliersViewModel>(shell.Page);
                var combo = new ComboBox { ItemsSource = page.FilterSuppliers };
                combo.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(SuppliersViewModel.FilterSupplier))
                    { Source = page, Mode = BindingMode.TwoWay });
                page.FilterSupplier = page.FilterSuppliers.Single(s => s.Id == id);
                Assert.Equal(id, Assert.IsType<SupplierItem>(combo.SelectedItem).Id);

                page.OpenCommand.Execute(page.Pager.Items.Single(s => s.Id == id));

                Assert.NotNull(page.Details);
                Assert.Equal(id, page.FilterSupplier.Id);
                Assert.Equal(id, Assert.IsType<SupplierItem>(combo.SelectedItem).Id);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Supplier filter check timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
