using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalCashier.Persistence;

namespace LocalCashier.Printing;

public static class StockDocuments
{
    public static IReadOnlyList<FrameworkElement> CountSheet(CountSessionRow session, IReadOnlyList<StockCountLineRow> lines, bool arabic, IReadOnlyDictionary<string,string> labels)
    {
        var pages=new List<FrameworkElement>();
        foreach(var chunk in lines.Chunk(19))
        {
            var body=new StackPanel();
            body.Children.Add(new TextBlock {Text=$"{labels["Stock_Counts"]} #{session.Id} · {session.Location}",FontSize=22,Margin=new Thickness(0,0,0,20)});
            void Row(string product,string batch,string counted,bool header=false)
            {
                var grid=new Grid {Margin=new Thickness(0,0,0,8)};
                foreach(var width in new[]{390d,190d,140d})grid.ColumnDefinitions.Add(new(){Width=new GridLength(width)});
                var values=new[]{product,batch,counted};
                for(var i=0;i<3;i++)
                {
                    var text=new TextBlock { Text=values[i],FontSize=12,Height=34,TextTrimming=TextTrimming.CharacterEllipsis,
                        FlowDirection=i==2?FlowDirection.LeftToRight:arabic?FlowDirection.RightToLeft:FlowDirection.LeftToRight,FontWeight=header?FontWeights.Bold:FontWeights.Normal };
                    Grid.SetColumn(text,i);grid.Children.Add(text);
                }
                body.Children.Add(grid);
            }
            Row(labels["Col_Product"],labels["Stock_Batch"],labels["Stock_Counted"],true);
            foreach(var line in chunk)Row(line.Product,line.Batch??"","________________");
            var page=new Border {Width=793.7,Height=1122.5,Padding=new Thickness(34),Background=Brushes.White,Child=body,FlowDirection=arabic?FlowDirection.RightToLeft:FlowDirection.LeftToRight};
            page.Measure(new Size(page.Width,page.Height));page.Arrange(new Rect(0,0,page.Width,page.Height));page.UpdateLayout();pages.Add(page);
        }
        return pages;
    }
}
