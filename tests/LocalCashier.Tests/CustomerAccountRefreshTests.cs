using System.Runtime.ExceptionServices;
using Dukkan.UI;
using LocalCashier.Application;
using LocalCashier.Domain;

namespace LocalCashier.Tests;

public class CustomerAccountRefreshTests
{
    [Fact]
    public void Repeated_account_changes_refresh_the_visible_balance_and_statement()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var folder = new TempFolder();
                var services = AppServices.Create(folder.Combine("account-refresh"), new FakePrinter());
                var owner = services.Users.Create("owner", "Owner", Role.Owner, "owner-test-1");
                services.Settings.ConfigureTax(owner, true);
                var customerId = services.Customers.Add(owner, "Credit customer", null, true, 5000m);
                var shell = new ShellViewModel(services);
                shell.SignedIn(services.Users.Login("owner", "owner-test-1").User!);
                shell.Nav = "Customers";
                var page = Assert.IsType<CustomersViewModel>(shell.Page);
                page.OpenCommand.Execute(page.Pager.Items.Single(c => c.Id == customerId));

                foreach (var amount in new[] { 100m, 200m, 300m })
                {
                    page.Details!.OpeningCommand.Execute(null);
                    var dialog = Assert.IsType<CustomerOpeningViewModel>(shell.Dialog);
                    dialog.Amount = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    dialog.SaveCommand.Execute(null);
                    Assert.False(shell.HasDialog);
                    var actualBalance = services.Customers.List().Single(c => c.Id == customerId).Balance;
                    Assert.Equal(actualBalance, page.Details!.Customer.Outstanding);
                    Assert.Equal(actualBalance, page.Details.Statement.First().Running);
                }
                Assert.Equal(600m, page.Details!.Customer.Outstanding);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Customer account check timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
