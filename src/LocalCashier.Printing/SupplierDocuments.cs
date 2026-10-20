using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalCashier.Persistence;

namespace LocalCashier.Printing;

public static class SupplierDocuments
{
    public static IReadOnlyList<FrameworkElement> PurchaseOrder(PurchaseOrderRow order, IReadOnlyList<PurchaseOrderLineRow> lines,
        string store, bool arabic, IReadOnlyDictionary<string, string> labels)
    {
        var pages = new List<FrameworkElement>();
        foreach (var chunk in lines.Chunk(22))
        {
            var body = new StackPanel();
            TextBlock Text(string value, double size = 12, bool numeric = false) => new()
            {
                Text = value, FontSize = size, FontFamily = new("Segoe UI, Tahoma"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 5), FlowDirection = numeric ? FlowDirection.LeftToRight : arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            };
            body.Children.Add(Text(store, 24));
            body.Children.Add(Text(labels["PO_Title"] + " " + order.Number, 18));
            body.Children.Add(Text(labels["Col_Supplier"] + ": " + order.Supplier));
            body.Children.Add(Text(labels["PO_Expected"] + ": " + (order.ExpectedDate ?? "")));
            body.Children.Add(Text(labels["PO_NoPosting"]));
            void Row(string description, string qty, string cost, string total, bool header)
            {
                var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                foreach (var width in new[] { 340d, 85d, 100d, 100d }) grid.ColumnDefinitions.Add(new() { Width = new GridLength(width) });
                var values = new[] { description, qty, cost, total };
                for (var n = 0; n < values.Length; n++)
                {
                    var text = Text(values[n], 12, n > 0); text.FontWeight = header ? FontWeights.Bold : FontWeights.Normal;
                    Grid.SetColumn(text, n); grid.Children.Add(text);
                }
                body.Children.Add(grid);
            }
            Row(labels["Col_Item"], labels["Col_Qty"], labels["Col_Cost"], labels["Col_Total"], true);
            foreach (var line in chunk) Row(line.Description, line.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                line.UnitCost.ToString("N4", CultureInfo.InvariantCulture), (line.Quantity * line.UnitCost).ToString("N2", CultureInfo.InvariantCulture), false);
            body.Children.Add(Text(labels["Col_Total"] + ": " + order.Total.ToString("N2", CultureInfo.InvariantCulture), 16));
            body.Children.Add(Text(order.Notes ?? ""));
            var page = new Border { Width = 793.7, Height = 1122.5, Background = Brushes.White, Padding = new Thickness(34), Child = body,
                FlowDirection = arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
            page.Measure(new Size(page.Width, page.Height));
            page.Arrange(new Rect(0, 0, page.Width, page.Height));
            page.UpdateLayout();
            pages.Add(page);
        }
        return pages;
    }
}
