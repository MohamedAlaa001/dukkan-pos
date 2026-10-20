using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalCashier.Persistence;

namespace LocalCashier.Printing;

public static class DailyDocuments
{
    public static IReadOnlyList<FrameworkElement> Build(DailySummaryReport report,string store,bool arabic,bool a4,IReadOnlyDictionary<string,string> labels)
    {
        var body=new StackPanel {Width=a4?700:274,FlowDirection=arabic?FlowDirection.RightToLeft:FlowDirection.LeftToRight};
        void Heading(string text)=>body.Children.Add(new TextBlock{Text=text,FontSize=a4?18:13,FontWeight=FontWeights.Bold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)});
        void Row(string name,string value)
        {
            var grid=new Grid{Margin=new Thickness(0,2,0,2)};grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(a4?150:90)});
            var caption=new TextBlock{Text=name,FontSize=a4?12:10,TextWrapping=TextWrapping.Wrap};var amount=new TextBlock{Text=value,FontSize=a4?12:10,FlowDirection=FlowDirection.LeftToRight,TextAlignment=TextAlignment.Right,TextWrapping=TextWrapping.Wrap};Grid.SetColumn(amount,1);grid.Children.Add(caption);grid.Children.Add(amount);body.Children.Add(grid);
        }
        string Num(decimal value)=>value.ToString("N2",CultureInfo.InvariantCulture);
        Heading(store);Heading(labels["Daily_Title"]+" · "+report.Date);
        foreach(var (key,value) in new (string,decimal)[]{("Daily_Sales",report.Sales),("Smart_Returns",report.Returns),("Smart_Revenue",report.Revenue),("Smart_TaxAmount",report.Tax),("Daily_Expenses",report.Expenses),("Daily_Opening",report.OpeningCash),("Daily_Expected",report.ExpectedCash)})Row(labels[key],Num(value));
        Row(labels["Daily_Counted"],report.CountedCash is decimal n?Num(n):labels["Daily_Uncounted"]);Row(labels["Daily_OpenShifts"],report.OpenShifts.ToString(CultureInfo.InvariantCulture));
        Heading(labels["Daily_Methods"]);foreach(var method in report.Methods)Row(arabic?method.NameAr:method.NameEn,Num(method.Net));
        Heading(labels["Daily_Top"]);foreach(var item in report.TopItems)Row(arabic&&!string.IsNullOrWhiteSpace(item.NameAr)?item.NameAr!:item.NameEn,item.Quantity.ToString("0.###",CultureInfo.InvariantCulture));
        body.Children.Add(new TextBlock{Text=labels["Daily_Hint"],FontSize=a4?10:8,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});
        body.Measure(new Size(body.Width,double.PositiveInfinity));
        var page=new Border{Width=a4?793.7:302.36,Height=a4?1122.5:body.DesiredSize.Height+28,Padding=new Thickness(a4?40:14),Background=Brushes.White};
        page.Child=a4?new Viewbox{Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,VerticalAlignment=VerticalAlignment.Top,Child=body}:body;
        page.Measure(new Size(page.Width,page.Height));page.Arrange(new Rect(0,0,page.Width,page.Height));page.UpdateLayout();return [page];
    }
}
