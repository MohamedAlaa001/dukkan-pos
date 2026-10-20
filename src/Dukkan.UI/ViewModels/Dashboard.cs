using System.Collections.ObjectModel;
using System.Globalization;

namespace Dukkan.UI;

public sealed class DeltaInfo
{
    public string Text { get; init; } = "";
    public BadgeKind Kind { get; init; } = BadgeKind.Neutral;
}

public sealed class DashboardViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    Option _range;
    bool _loading;
    DashboardStatsView _view = new();

    public DashboardViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Ranges = new[] {"Range_Today", "Range_7d", "Range_30d"}.Select(k => new Option(k)).ToList();
        _range = Ranges[0];
        Categories = [];
        Reload();
        Localizer.Instance.PropertyChanged += (_, _) => Raise(string.Empty);
    }

    public Localizer L => Localizer.Instance;
    public IReadOnlyList<Option> Ranges { get; }
    public ObservableCollection<CategoryShare> Categories { get; }
    public bool IsEmpty => _view.Invoices == 0;

    public Option Range
    {
        get => _range;
        set { if (Set(ref _range, value)) ShellViewModel.SimulateLoading(b => IsLoading = b, Reload, 250); }
    }

    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public decimal TotalSales => _view.Sales;
    public decimal Invoices => _view.Invoices;
    public decimal ItemsSold => _view.Units;
    public decimal GrossProfit => _view.Profit;
    public DeltaInfo SalesDelta => _view.SalesDelta;
    public DeltaInfo InvoicesDelta => _view.InvoicesDelta;
    public DeltaInfo ItemsDelta => _view.ItemsDelta;
    public DeltaInfo ProfitDelta => _view.ProfitDelta;
    public string DateText => DateTime.ParseExact(_shell.S.Queries.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("ddd, MMM d, yyyy", ShellViewModel.Culture);

    public IReadOnlyList<ChartPoint> Trend => _view.Trend.Select(d => new ChartPoint(
        _view.Trend.Count <= 7 ? DateTime.Parse(d.Date, CultureInfo.InvariantCulture).ToString("ddd", ShellViewModel.Culture) : DateTime.Parse(d.Date, CultureInfo.InvariantCulture).ToString("dd", CultureInfo.InvariantCulture),
        (double)d.Sales)).ToList();

    public string TrendTitle => L[_range.Key == "Range_30d" ? "Dash_Trend30" : "Dash_Trend"];

    void Reload()
    {
        var q = _shell.S.Queries;
        var today = DateTime.ParseExact(q.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = _range.Key switch { "Range_30d" => 30, "Range_7d" => 7, _ => 1 };
        string F(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var from = today.AddDays(-(days - 1));
        var prevTo = from.AddDays(-1);
        var prevFrom = prevTo.AddDays(-(days - 1));
        var cur = q.Stats(F(from), F(today));
        var prev = q.Stats(F(prevFrom), F(prevTo));
        var trendDays = _range.Key == "Range_30d" ? 30 : 7;
        var key = _range.Key == "Range_Today" ? "Dash_DeltaDay" : "Dash_DeltaPeriod";

        DeltaInfo D(decimal now, decimal before)
        {
            if (before <= 0) return new DeltaInfo { Text = "—", Kind = BadgeKind.Neutral };
            var pct = (now - before) / before * 100m;
            var text = string.Format(L[key], Math.Abs(Math.Round(pct)).ToString("0", CultureInfo.InvariantCulture));
            return new DeltaInfo { Text = text, Kind = pct >= 0 ? BadgeKind.Success : BadgeKind.Danger };
        }

        _view = new DashboardStatsView
        {
            Sales = cur.Sales, Invoices = cur.Invoices, Units = cur.Units, Profit = cur.GrossProfit,
            SalesDelta = D(cur.Sales, prev.Sales), InvoicesDelta = D(cur.Invoices, prev.Invoices), ItemsDelta = D(cur.Units, prev.Units), ProfitDelta = D(cur.GrossProfit, prev.GrossProfit),
            Trend = q.Daily(F(today.AddDays(-(trendDays - 1))), F(today)),
        };

        Categories.Clear();
        foreach (var c in q.Categories(F(from), F(today)).Take(5)) Categories.Add(new CategoryShare { Key = c.CategoryKey, Percent = c.Percent });
        Raise(string.Empty);
    }

    sealed class DashboardStatsView
    {
        public decimal Sales, Profit;
        public int Invoices, Units;
        public DeltaInfo SalesDelta = new(), InvoicesDelta = new(), ItemsDelta = new(), ProfitDelta = new();
        public IReadOnlyList<LocalCashier.Persistence.DailyPoint> Trend = [];
    }
}
