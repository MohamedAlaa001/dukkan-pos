using System.Windows.Input;

namespace Dukkan.UI;

/// <summary>Routes the header query to the existing, permission-aware search screens.</summary>
public sealed class GlobalSearchViewModel(ShellViewModel shell, string query) : DialogViewModel(shell)
{
    public string Query { get; } = query;
    public bool CanSearchInvoices => Shell.CanAccess("Reports");
    public ICommand ProductsCommand => new RelayCommand(() =>
    {
        Shell.CloseDialog();
        Shell.Nav = "Products";
        if (Shell.Page is ProductsViewModel products)
        {
            products.ClearFiltersCommand.Execute(null);
            products.Search = Query;
        }
    });
    public ICommand CustomersCommand => new RelayCommand(() =>
    {
        Shell.CloseDialog();
        Shell.Nav = "Customers";
        if (Shell.Page is CustomersViewModel customers)
        {
            customers.BackCommand.Execute(null);
            customers.Search = Query;
        }
    });
    public ICommand InvoicesCommand => new RelayCommand(() =>
    {
        Shell.CloseDialog();
        Shell.Nav = "Reports";
        if (Shell.Page is ReportsViewModel reports)
        {
            reports.Tab = 1;
            reports.StatusFilter = reports.StatusFilters[0];
            reports.PayFilter = reports.PayFilters[0];
            reports.Search = Query;
        }
    }, () => CanSearchInvoices);
}
