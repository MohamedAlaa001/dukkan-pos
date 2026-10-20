using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Dukkan.UI.Views;

// Views are XAML-only; these partial classes exist solely to host InitializeComponent and a few presentation handlers.
public partial class DashboardView : UserControl { public DashboardView() => InitializeComponent(); }

public partial class PosView : UserControl { public PosView() => InitializeComponent(); }

public partial class ProductEditView : UserControl { public ProductEditView() => InitializeComponent(); }

public partial class InventoryView : UserControl { public InventoryView() => InitializeComponent(); }

public partial class ExpensesView : UserControl { public ExpensesView() => InitializeComponent(); }

public partial class SuppliersView : UserControl { public SuppliersView() => InitializeComponent(); }

public partial class ReportsView : UserControl { public ReportsView() => InitializeComponent(); }

public partial class SettingsView : UserControl { public SettingsView() => InitializeComponent(); }

public partial class SetupView : UserControl { public SetupView() => InitializeComponent(); }

public partial class PaginationBar : UserControl { public PaginationBar() => InitializeComponent(); }

public partial class ProductsView : UserControl
{
    public ProductsView() => InitializeComponent();

    // Closes the row-action popup after one of its buttons is used.
    void CloseMenu(object sender, RoutedEventArgs e)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d is not null && d is not Popup) d = LogicalTreeHelper.GetParent(d) ?? System.Windows.Media.VisualTreeHelper.GetParent(d);
        if (d is Popup p) p.IsOpen = false;
    }
}

public partial class CustomersView : UserControl
{
    public CustomersView() => InitializeComponent();

    void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { Item: CustomerItem c } && DataContext is CustomersViewModel vm) vm.OpenCommand.Execute(c);
    }

    void Row_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is DataGridRow { Item: CustomerItem c } && DataContext is CustomersViewModel vm) { vm.OpenCommand.Execute(c); e.Handled = true; }
    }
}
