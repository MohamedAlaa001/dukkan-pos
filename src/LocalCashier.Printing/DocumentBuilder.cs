using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace LocalCashier.Printing;

public sealed record StoreInfo(string Name, string? Phone, string? Address, string? Footer, bool Arabic);

public sealed record LabelItem(string Name, string? Size, string? Color, decimal Price, string Barcode, string Sku, string CurrencyLabel);

/// <summary>
/// Builds print documents as WPF visuals from immutable sale snapshots. Sizes are in WPF device-independent units (1/96 inch);
/// the same visual is sent to the printer, rendered to PDF, or previewed.
/// </summary>
public static class DocumentBuilder
{
    public const double MmToDip = 96 / 25.4;

    static readonly FontFamily Font = new("Segoe UI, Tahoma");

    static readonly Dictionary<string, (string En, string Ar)> T = new()
    {
        ["copy"] = ("*** COPY ***", "*** نسخة ***"),
        ["receipt"] = ("Receipt", "إيصال"),
        ["invoice"] = ("Invoice", "فاتورة"),
        ["cashier"] = ("Cashier", "الكاشير"),
        ["date"] = ("Date", "التاريخ"),
        ["subtotal"] = ("Subtotal", "المجموع الفرعي"),
        ["discount"] = ("Discount", "الخصم"),
        ["tax"] = ("Tax", "الضريبة"),
        ["total"] = ("Total", "الإجمالي"),
        ["cash"] = ("Cash", "نقدًا"),
        ["card"] = ("Visa", "فيزا"),
        ["transfer"] = ("Instapay", "إنستاباي"),
        ["change"] = ("Change", "الباقي"),
        ["balance"] = ("Balance due", "المتبقي على الحساب"),
        ["thanks"] = ("Thank you!", "شكرًا لزيارتكم!"),
        ["item"] = ("Item", "الصنف"),
        ["qty"] = ("Qty", "الكمية"),
        ["price"] = ("Price", "السعر"),
        ["page"] = ("Page", "صفحة"),
        ["customer"] = ("Customer", "العميل"),
        ["reference"] = ("Reference", "المرجع"),
        ["approval"] = ("Approval", "الموافقة"),
        ["last4"] = ("Last 4", "آخر 4"),
        ["offers"] = ("Included offers", "العروض المشمولة"),
        ["pointsEarned"] = ("Points earned", "نقاط مكتسبة"),
        ["pointsBalance"] = ("Points after this sale", "النقاط بعد هذا البيع"),
        ["pointsRedeemed"] = ("Points redeemed", "نقاط مستبدلة"),
    };

    static string L(string key, bool ar) => T.TryGetValue(key, out var v) ? (ar ? v.Ar : v.En) : key;

    static string Cur(bool ar) => ar ? "ج.م" : "EGP";

    static string Num(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);

    static TextBlock Text(string text, double size = 12.5, FontWeight? weight = null, TextAlignment align = TextAlignment.Left,
        Brush? brush = null, bool ltr = false, bool wrap = true, Thickness? margin = null)
    {
        var tb = new TextBlock
        {
            Text = text, FontFamily = Font, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextAlignment = align,
            Foreground = brush ?? Brushes.Black, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = margin ?? new Thickness(0),
        };
        if (ltr) tb.FlowDirection = FlowDirection.LeftToRight;
        return tb;
    }

    static UIElement Rule(double top = 4, double bottom = 4, bool dashed = true)
    {
        var line = new Line2(dashed) { Margin = new Thickness(0, top, 0, bottom) };
        return line;
    }

    sealed class Line2 : FrameworkElement
    {
        readonly bool _dashed;
        public Line2(bool dashed) { _dashed = dashed; Height = 1; }

        protected override void OnRender(DrawingContext dc)
        {
            var pen = new Pen(Brushes.Black, 1) { DashStyle = _dashed ? new DashStyle([3, 3], 0) : DashStyles.Solid };
            dc.DrawLine(pen, new Point(0, 0.5), new Point(ActualWidth, 0.5));
        }
    }

    static Grid Row(UIElement left, UIElement right, double rightWidth = double.NaN)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(rightWidth) ? GridLength.Auto : new GridLength(rightWidth) });
        Grid.SetColumn(right, 1);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    static string MethodLabel(string method, bool ar) => L(method.ToLowerInvariant(), ar);
    static string MethodLabel(ReceiptPayment pay,bool ar)=>(ar?pay.NameAr:pay.NameEn)??MethodLabel(pay.Method,ar);
    static string PaymentDetail(ReceiptPayment pay,bool ar)=>string.Join(" · ",new[]{
        string.IsNullOrWhiteSpace(pay.Reference)?null:L("reference",ar)+": "+pay.Reference,
        string.IsNullOrWhiteSpace(pay.ApprovalCode)?null:L("approval",ar)+": "+pay.ApprovalCode,
        string.IsNullOrWhiteSpace(pay.Last4)?null:L("last4",ar)+": ****"+pay.Last4}.Where(s=>s is not null));

    static string Variant(ReceiptLine l) =>
        string.Join(" / ", new[] { l.Size, l.Color }.Where(x => !string.IsNullOrWhiteSpace(x)));

    // ---------------------------------------------------------------- 80 mm receipt
    public static FrameworkElement Receipt80(ReceiptModel r, StoreInfo store, bool isCopy)
    {
        var ar = store.Arabic;
        var root = new StackPanel
        {
            Width = 72 * MmToDip, Background = Brushes.White, FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Margin = new Thickness(0),
        };
        var cur = Cur(ar);

        root.Children.Add(Text(store.Name, 17, FontWeights.Bold, TextAlignment.Center));
        if (!string.IsNullOrWhiteSpace(store.Address)) root.Children.Add(Text(store.Address!, 11, align: TextAlignment.Center));
        if (!string.IsNullOrWhiteSpace(store.Phone)) root.Children.Add(Text(store.Phone!, 11, align: TextAlignment.Center, ltr: true));
        if (isCopy) root.Children.Add(Text(L("copy", ar), 13, FontWeights.Bold, TextAlignment.Center, margin: new Thickness(0, 6, 0, 0)));
        root.Children.Add(Rule(6, 4));

        root.Children.Add(Row(Text(L("receipt", ar), 12), Text(r.Number, 12, FontWeights.SemiBold, TextAlignment.Right, ltr: true)));
        root.Children.Add(Row(Text(L("date", ar), 12), Text(DateTimeOffset.Parse(r.CreatedAt, CultureInfo.InvariantCulture).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), 12, align: TextAlignment.Right, ltr: true)));
        root.Children.Add(Row(Text(L("cashier", ar), 12), Text(r.Cashier, 12, align: TextAlignment.Right)));
        if (!string.IsNullOrWhiteSpace(r.Customer)) root.Children.Add(Row(Text(L("customer", ar), 12), Text(r.Customer!, 12, align: TextAlignment.Right)));
        root.Children.Add(Rule(4, 6));

        foreach (var l in r.Lines)
        {
            root.Children.Add(Text(l.Name, 12.5, FontWeights.SemiBold));
            var variant = Variant(l);
            var detail = $"{l.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} × {Num(l.UnitPrice)}" + (variant.Length > 0 ? $"   {variant}" : "");
            root.Children.Add(Row(Text(detail, 11.5, brush: Brushes.Black, ltr: false), Text(Num(l.Total), 12.5, FontWeights.SemiBold, TextAlignment.Right, ltr: true)));
            root.Children.Add(new Border { Height = 5 });
        }

        root.Children.Add(Rule(2, 4));
        var gross = r.Lines.Sum(l => Money.Round(l.Quantity * l.UnitPrice));
        root.Children.Add(Row(Text(L("subtotal", ar), 12), Text(Num(gross), 12, align: TextAlignment.Right, ltr: true)));
        if (r.Discount > 0) root.Children.Add(Row(Text(L("discount", ar), 12), Text("-" + Num(r.Discount), 12, align: TextAlignment.Right, ltr: true)));
        if(r.Promotions is {Count:>0})
        {
            root.Children.Add(Text(L("offers",ar),10,brush:Brushes.DimGray));
            foreach(var offer in r.Promotions)root.Children.Add(Row(Text(ar?offer.NameAr:offer.NameEn,10),Text("-"+Num(offer.Amount),10,align:TextAlignment.Right,ltr:true)));
        }
        if (r.Tax > 0) root.Children.Add(Row(Text(L("tax", ar), 12), Text(Num(r.Tax), 12, align: TextAlignment.Right, ltr: true)));
        root.Children.Add(Rule(3, 3, dashed: false));
        root.Children.Add(Row(Text(L("total", ar), 15, FontWeights.Bold), Text($"{cur} {Num(r.Total)}", 15, FontWeights.Bold, TextAlignment.Right, ltr: true)));
        root.Children.Add(Rule(3, 4));

        foreach (var p in r.Payments)
        {
            root.Children.Add(Row(Text(MethodLabel(p, ar), 12), Text(Num(p.Tendered), 12, align: TextAlignment.Right, ltr: true)));
            if(PaymentDetail(p,ar) is {Length:>0} detail)root.Children.Add(Text(detail,10,ltr:true));
        }

        if (r.Change > 0) root.Children.Add(Row(Text(L("change", ar), 12, FontWeights.SemiBold), Text(Num(r.Change), 12, FontWeights.SemiBold, TextAlignment.Right, ltr: true)));
        if (r.Debt > 0) root.Children.Add(Row(Text(L("balance", ar), 12, FontWeights.Bold), Text($"{cur} {Num(r.Debt)}", 12, FontWeights.Bold, TextAlignment.Right, ltr: true)));
        if(r.HasLoyalty)
        {
            root.Children.Add(Row(Text(L("pointsEarned",ar),11),Text(r.EarnedPoints.ToString(CultureInfo.InvariantCulture),11,align:TextAlignment.Right,ltr:true)));
            if(r.RedeemedPoints>0)root.Children.Add(Row(Text(L("pointsRedeemed",ar),11),Text(r.RedeemedPoints.ToString(CultureInfo.InvariantCulture),11,align:TextAlignment.Right,ltr:true)));
            root.Children.Add(Row(Text(L("pointsBalance",ar),11),Text(r.PointsBalance.ToString(CultureInfo.InvariantCulture),11,align:TextAlignment.Right,ltr:true)));
        }

        root.Children.Add(Rule(8, 6));
        root.Children.Add(Text(string.IsNullOrWhiteSpace(store.Footer) ? L("thanks", ar) : store.Footer!, 12, align: TextAlignment.Center));
        root.Children.Add(new Border { Height = 24 });
        Measure(root);
        return root;
    }

    // ---------------------------------------------------------------- A4 invoice (possibly several pages)
    public static IReadOnlyList<FrameworkElement> InvoiceA4(ReceiptModel r, StoreInfo store, bool isCopy, int linesPerPage = 26)
    {
        var ar = store.Arabic;
        var pages = new List<FrameworkElement>();
        var chunks = r.Lines.Chunk(linesPerPage).DefaultIfEmpty([]).ToList();
        for (var p = 0; p < chunks.Count; p++)
        {
            var last = p == chunks.Count - 1;
            var page = new Grid
            {
                Width = 210 * MmToDip, Height = 297 * MmToDip, Background = Brushes.White,
                FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            };
            var body = new StackPanel { Margin = new Thickness(18 * MmToDip, 16 * MmToDip, 18 * MmToDip, 16 * MmToDip) };
            page.Children.Add(body);

            body.Children.Add(Row(
                new StackPanel
                {
                    Children =
                    {
                        Text(store.Name, 24, FontWeights.Bold),
                        Text(store.Address ?? "", 11, brush: Brushes.DimGray),
                        Text(store.Phone ?? "", 11, brush: Brushes.DimGray, ltr: true),
                    },
                },
                new StackPanel
                {
                    Children =
                    {
                        Text(L("invoice", ar), 20, FontWeights.SemiBold, TextAlignment.Right),
                        Text(r.Number, 13, FontWeights.SemiBold, TextAlignment.Right, ltr: true),
                        Text(DateTimeOffset.Parse(r.CreatedAt, CultureInfo.InvariantCulture).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), 11, FontWeights.Normal, TextAlignment.Right, Brushes.DimGray, ltr: true),
                        Text($"{L("cashier", ar)}: {r.Cashier}", 11, align: TextAlignment.Right, brush: Brushes.DimGray),
                        Text(string.IsNullOrWhiteSpace(r.Customer) ? "" : $"{L("customer", ar)}: {r.Customer}", 11, align: TextAlignment.Right, brush: Brushes.DimGray),
                    },
                }));
            if (isCopy) body.Children.Add(Text(L("copy", ar), 14, FontWeights.Bold, TextAlignment.Center, margin: new Thickness(0, 8, 0, 0)));
            body.Children.Add(new Border { Height = 18 });

            // table header
            body.Children.Add(TableRow(L("item", ar), L("qty", ar), L("price", ar), L("total", ar), header: true));
            foreach (var l in chunks[p])
            {
                var variant = Variant(l);
                body.Children.Add(TableRow(variant.Length > 0 ? $"{l.Name}  ({variant})" : l.Name, l.Quantity.ToString("0.###", CultureInfo.InvariantCulture), Num(l.UnitPrice), Num(l.Total), header: false));
            }

            if (last)
            {
                body.Children.Add(new Border { Height = 14 });
                var gross = r.Lines.Sum(l => Money.Round(l.Quantity * l.UnitPrice));
                var totals = new StackPanel { Width = 80 * MmToDip, HorizontalAlignment = HorizontalAlignment.Right };
                totals.Children.Add(Row(Text(L("subtotal", ar)), Text(Num(gross), align: TextAlignment.Right, ltr: true)));
                if (r.Discount > 0) totals.Children.Add(Row(Text(L("discount", ar)), Text("-" + Num(r.Discount), align: TextAlignment.Right, ltr: true)));
                if(r.Promotions is {Count:>0})
                {
                    totals.Children.Add(Text(L("offers",ar),10,brush:Brushes.DimGray));
                    foreach(var offer in r.Promotions)totals.Children.Add(Row(Text(ar?offer.NameAr:offer.NameEn,10),Text("-"+Num(offer.Amount),10,align:TextAlignment.Right,ltr:true)));
                }
                if (r.Tax > 0) totals.Children.Add(Row(Text(L("tax", ar)), Text(Num(r.Tax), align: TextAlignment.Right, ltr: true)));
                totals.Children.Add(Rule(4, 4, dashed: false));
                totals.Children.Add(Row(Text(L("total", ar), 16, FontWeights.Bold), Text($"{Cur(ar)} {Num(r.Total)}", 16, FontWeights.Bold, TextAlignment.Right, ltr: true)));
                foreach (var pay in r.Payments)
                {
                    totals.Children.Add(Row(Text(MethodLabel(pay, ar)), Text(Num(pay.Tendered), align: TextAlignment.Right, ltr: true)));
                    if(PaymentDetail(pay,ar) is {Length:>0} detail)totals.Children.Add(Text(detail,10,ltr:true));
                }
                if (r.Change > 0) totals.Children.Add(Row(Text(L("change", ar)), Text(Num(r.Change), align: TextAlignment.Right, ltr: true)));
                if (r.Debt > 0) totals.Children.Add(Row(Text(L("balance", ar), weight: FontWeights.Bold), Text($"{Cur(ar)} {Num(r.Debt)}", weight: FontWeights.Bold, align: TextAlignment.Right, ltr: true)));
                if(r.HasLoyalty)
                {
                    totals.Children.Add(Row(Text(L("pointsEarned",ar),11),Text(r.EarnedPoints.ToString(CultureInfo.InvariantCulture),11,align:TextAlignment.Right,ltr:true)));
                    if(r.RedeemedPoints>0)totals.Children.Add(Row(Text(L("pointsRedeemed",ar),11),Text(r.RedeemedPoints.ToString(CultureInfo.InvariantCulture),11,align:TextAlignment.Right,ltr:true)));
                    totals.Children.Add(Row(Text(L("pointsBalance",ar),11),Text(r.PointsBalance.ToString(CultureInfo.InvariantCulture),11,align:TextAlignment.Right,ltr:true)));
                }
                // Keep each totals/tender row independently pageable, including wrapped metadata.
                while(totals.Children.Count>0)
                {
                    var entry=totals.Children[0];totals.Children.RemoveAt(0);
                    body.Children.Add(new StackPanel { Width=80*MmToDip,HorizontalAlignment=HorizontalAlignment.Right,Children={entry} });
                }
                body.Children.Add(Text(string.IsNullOrWhiteSpace(store.Footer) ? L("thanks", ar) : store.Footer!, 12, align: TextAlignment.Center, margin: new Thickness(0, 30, 0, 0)));
            }

            // Item count alone cannot bound a page: names, payment names and references wrap.
            // Reflow measured rows before rendering so every tender remains on paper/PDF.
            Measure(page);
            var entries=body.Children.Cast<UIElement>().Select(e=>(Element:e,Height:e.DesiredSize.Height)).ToArray();body.Children.Clear();
            var current=page;var currentBody=body;var used=0d;
            var contentWidth=page.Width-body.Margin.Left-body.Margin.Right;
            var contentHeight=page.Height-body.Margin.Top-body.Margin.Bottom;
            foreach(var measured in entries)
            {
                var entry=measured.Element;var entryHeight=measured.Height;
                if(used+entryHeight>contentHeight && currentBody.Children.Count>0)
                {
                    Measure(current);pages.Add(current);
                    current=new Grid {Width=page.Width,Height=page.Height,Background=Brushes.White,FlowDirection=page.FlowDirection};
                    currentBody=new StackPanel {Margin=body.Margin};current.Children.Add(currentBody);
                    var heading=Text($"{L("invoice",ar)} {r.Number}",16,FontWeights.Bold,margin:new Thickness(0,0,0,12));
                    currentBody.Children.Add(heading);Measure(current);used=heading.DesiredSize.Height;
                }
                currentBody.Children.Add(entry);used+=entryHeight;
            }
            Measure(current);pages.Add(current);
        }

        if(pages.Count>1)
            for(var i=0;i<pages.Count;i++)
            {
                ((Grid)pages[i]).Children.Add(new TextBlock
                {
                    Text=$"{L("page",ar)} {i+1} / {pages.Count}",FontFamily=Font,FontSize=10,Foreground=Brushes.DimGray,
                    HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,8*MmToDip)
                });
                Measure(pages[i]);
            }

        return pages;
    }

    static Grid TableRow(string item, string qty, string price, string total, bool header)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var w in new[] { 22, 28, 32 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w * MmToDip) });
        var weight = header ? FontWeights.Bold : FontWeights.Normal;
        g.Children.Add(Text(item, 12, weight));
        void Cell(string t, int col)
        {
            var tb = Text(t, 12, weight, TextAlignment.Right, ltr: !header);
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }

        Cell(qty, 1); Cell(price, 2); Cell(total, 3);
        return new Grid
        {
            Children = { new Border { BorderBrush = header ? Brushes.Black : Brushes.LightGray, BorderThickness = new Thickness(0, header ? 1 : 0, 0, 1), Padding = new Thickness(0, 5, 0, 5), Child = g } },
        };
    }

    // ---------------------------------------------------------------- Barcode labels (EAN-13)
    public static FrameworkElement Label(LabelItem item, double widthMm = 50, double heightMm = 30, bool arabic = false)
    {
        var root = new Grid { Width = widthMm * MmToDip, Height = heightMm * MmToDip, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
        var stack = new StackPanel { Margin = new Thickness(2 * MmToDip, 1.5 * MmToDip, 2 * MmToDip, 1 * MmToDip) };
        root.Children.Add(stack);
        stack.Children.Add(Text(item.Name, 9.5, FontWeights.Bold, wrap: false, align: TextAlignment.Center));
        var variant = string.Join(" / ", new[] { item.Size, item.Color }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (variant.Length > 0) stack.Children.Add(Text(variant, 8.5, align: TextAlignment.Center, wrap: false));
        var barH = Math.Max(6, heightMm - 17) * MmToDip;
        stack.Children.Add(new EanBarcode(item.Barcode, widthMm - 4, barH) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1.5, 0, 0) });
        stack.Children.Add(Row(Text(item.Sku, 7.5, brush: Brushes.DimGray, wrap: false),
            Text($"{item.CurrencyLabel} {item.Price.ToString("N0", CultureInfo.InvariantCulture)}", 11, FontWeights.Bold, TextAlignment.Right, wrap: false)));
        Measure(root);
        return root;
    }

    /// <summary>A sheet of labels on A4 (columns × rows), starting at <paramref name="skip"/> labels in (to reuse a partly used sheet).</summary>
    public static IReadOnlyList<FrameworkElement> LabelSheetA4(IReadOnlyList<LabelItem> labels, int columns = 3, int rows = 8, int skip = 0)
    {
        var perPage = columns * rows;
        var all = Enumerable.Repeat<LabelItem?>(null, skip).Concat(labels).ToList();
        var pages = new List<FrameworkElement>();
        for (var start = 0; start < all.Count; start += perPage)
        {
            var grid = new Grid { Width = 210 * MmToDip, Height = 297 * MmToDip, Background = Brushes.White };
            var cellW = 210d / columns;
            var cellH = 297d / rows;
            for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellW * MmToDip) });
            for (var r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellH * MmToDip) });
            for (var i = 0; i < perPage && start + i < all.Count; i++)
            {
                if (all[start + i] is not { } item) continue;
                var cell = Label(item, cellW - 4, cellH - 4);
                cell.HorizontalAlignment = HorizontalAlignment.Center;
                cell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(cell, i % columns);
                Grid.SetRow(cell, i / columns);
                grid.Children.Add(cell);
            }

            Measure(grid);
            pages.Add(grid);
        }

        return pages;
    }

    static void Measure(FrameworkElement e)
    {
        e.Measure(new Size(double.IsNaN(e.Width) ? 4000 : e.Width, double.IsNaN(e.Height) ? double.PositiveInfinity : e.Height));
        e.Arrange(new Rect(e.DesiredSize));
        e.UpdateLayout();
    }
}

/// <summary>EAN-13 symbol drawn from the standard module pattern, with the human-readable digits underneath.</summary>
public sealed class EanBarcode : FrameworkElement
{
    readonly string _code;
    readonly double _widthDip;
    readonly double _barHeight;

    public EanBarcode(string code, double widthMm, double barHeightDip)
    {
        _code = code;
        _widthDip = widthMm * DocumentBuilder.MmToDip;
        _barHeight = barHeightDip;
        Width = _widthDip;
        Height = barHeightDip + 12;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Ean13.IsValid(_code)) return;
        var modules = Ean13.Modules(_code);
        const int quiet = 9;
        var module = Math.Floor((_widthDip / (95 + quiet * 2)) * 4) / 4;   // quarter-DIP steps keep bars crisp
        var x0 = (_widthDip - module * (95 + quiet * 2)) / 2 + module * quiet;
        var guard = new HashSet<int>([0, 1, 2, 45, 46, 47, 48, 49, 92, 93, 94]);
        for (var i = 0; i < modules.Length; i++)
        {
            if (modules[i] != '1') continue;
            var j = i;
            while (j + 1 < modules.Length && modules[j + 1] == '1') j++;
            var tall = guard.Contains(i);
            dc.DrawRectangle(Brushes.Black, null, new Rect(x0 + i * module, 0, (j - i + 1) * module, _barHeight + (tall ? 4 : 0)));
            i = j;
        }

        var typeface = new Typeface("Segoe UI");
        var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Digits(string s) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 8, Brushes.Black, ppd);
        var y = _barHeight + 1;
        dc.DrawText(Digits(_code[..1]), new Point(x0 - module * 8, y));
        dc.DrawText(Digits(_code.Substring(1, 6)), new Point(x0 + module * 3 + 2, y));
        dc.DrawText(Digits(_code.Substring(7, 6)), new Point(x0 + module * 50 + 2, y));
    }
}
