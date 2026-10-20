using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

public sealed class StockLevelItem : ObservableObject
{
    string _counted = "";

    public StockLevelItem(VariantRow v, string nameEn, string? nameAlt, GarmentKind kind, int alert)
    {
        Variant = new VariantItem(v, nameEn, nameAlt, kind);
        Alert = alert;
    }

    public VariantItem Variant { get; }
    /// <summary>Photo if the product has one, otherwise the category picture (null for clothing shops, which keep the garment art).</summary>
    public System.Windows.Media.ImageSource? Image { get; set; }
    public int Alert { get; }
    public bool IsCritical => Variant.Stock <= Alert;
    public StockStatus State => Variant.Stock <= 0 ? StockStatus.OutOfStock : Variant.Stock < 10 ? StockStatus.LowStock : StockStatus.Active;
    public string Status => Localizer.Instance["Status_" + State];
    public BadgeKind StatusKind => State switch { StockStatus.Active => BadgeKind.Success, StockStatus.LowStock => BadgeKind.Warning, _ => BadgeKind.Danger };
    public string Counted { get => _counted; set => Set(ref _counted, value); }
}

public sealed class InventoryViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    Option _type;
    Option _range;
    bool _loading;
    string _countReason = "";

    public InventoryViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Types = new[] {"Mov_All", "Mov_Sale", "Mov_Purchase", "Mov_Adjustment", "Mov_Return"}.Select(k => new Option(k)).ToList();
        Ranges = new[] {"Range_Today", "Range_7d", "Range_30d"}.Select(k => new Option(k)).ToList();
        _type = Types[0];
        _range = Ranges[1];
        Pager = new Pager<MovementItem>(7);
        Levels = [];
        AdjustCommand = new RelayCommand(() => _shell.ShowDialog(new StockAdjustViewModel(_shell, Reload)));
        PostCountCommand = new RelayCommand(PostCount);
        ReloadCommand = new RelayCommand(Reload);
        Reload();
    }

    public Localizer L => Localizer.Instance;
    public IReadOnlyList<Option> Types { get; }
    public IReadOnlyList<Option> Ranges { get; }
    public Pager<MovementItem> Pager { get; }
    public ObservableCollection<StockLevelItem> Levels { get; }
    public ICommand AdjustCommand { get; }
    public ICommand PostCountCommand { get; }
    public ICommand ReloadCommand { get; }
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public string CountReason { get => _countReason; set { if (Set(ref _countReason, value)) Raise(nameof(CountError)); } }
    public string CountError { get; private set; } = "";
    public Option Type { get => _type; set { if (Set(ref _type, value)) ShellViewModel.SimulateLoading(b => IsLoading = b, ApplyMovements, 200); } }
    public Option Range { get => _range; set { if (Set(ref _range, value)) ShellViewModel.SimulateLoading(b => IsLoading = b, ApplyMovements, 200); } }

    (string From, string To) Dates()
    {
        var today = DateTime.ParseExact(_shell.S.Queries.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = _range.Key switch { "Range_30d" => 30, "Range_7d" => 7, _ => 1 };
        return (today.AddDays(-(days - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    void Reload()
    {
        ApplyMovements();
        Levels.Clear();
        var cats = _shell.S.Styles.List(includeArchived: true).ToDictionary(s => s.Id, s => (s.CategoryKey, s.HasImage));
        foreach (var (v, n, a, k, alert) in _shell.S.Queries.StockLevels())
            Levels.Add(new StockLevelItem(v, n, a, Enum.TryParse<GarmentKind>(k, out var kk) ? kk : GarmentKind.TShirt, alert)
            {
                Image = (cats.TryGetValue(v.StyleId, out var c) && c.HasImage ? ProductImages.Get(v.StyleId) : null) ?? CategoryIcons.For(cats.TryGetValue(v.StyleId, out var c2) ? c2.CategoryKey : null),
            });
        _shell.RefreshNotifications();
    }

    void ApplyMovements()
    {
        var (from, to) = Dates();
        var key = _type.Key == "Mov_All" ? null : _type.Key["Mov_".Length..];
        Pager.SetSource(_shell.S.Queries.Movements(from, to, key).Select(m => new MovementItem(m)).ToList());
    }

    /// <summary>Posts one adjustment document for every row where a counted quantity was entered.</summary>
    void PostCount()
    {
        var l = Localizer.Instance;
        var entries = Levels.Where(x => !string.IsNullOrWhiteSpace(x.Counted)).ToList();
        if (entries.Count == 0) { CountError = l["Count_None"]; Raise(nameof(CountError)); return; }
        if (entries.Any(x => !int.TryParse(x.Counted, out var n) || n < 0)) { CountError = l["Err_Qty"]; Raise(nameof(CountError)); return; }
        if (string.IsNullOrWhiteSpace(_countReason)) { CountError = l["Err_Required"]; Raise(nameof(CountError)); return; }
        try
        {
            var items = entries.Select(x => new CountItem(x.Variant.ProductId, int.Parse(x.Counted), x.Variant.AvgCost > 0 ? x.Variant.AvgCost : x.Variant.Price * 0.6m)).ToList();
            _shell.S.Adjustments.ApplyCount(_shell.User!.Id, _countReason.Trim(), items);
            CountError = ""; Raise(nameof(CountError));
            CountReason = "";
            Reload();
            _shell.Toast(l["Toast_CountPosted"]);
        }
        catch (PosException ex) { CountError = _shell.ErrorText(ex); Raise(nameof(CountError)); }
    }
}

/// <summary>Receive a delivery (purchase) or post a one-off adjustment for a single variant found by barcode/SKU/name.</summary>
public sealed class StockAdjustViewModel : DialogViewModel
{
    readonly Action _done;
    string _query = "";
    string _qty = "", _cost = "", _reason = "", _reference = "";
    bool _fromRegister;
    VariantItem? _selected;
    SupplierItem? _selectedSupplier;
    bool _isPurchase = true;

    public StockAdjustViewModel(ShellViewModel shell, Action done) : base(shell)
    {
        _done = done;
        Results = [];
        Suppliers = shell.S.Suppliers.List().Where(s => s.Active).Select(s => new SupplierItem(s)).ToList();
        PickCommand = new RelayCommand(p => { var variant = (VariantItem)p!; Selected = variant; if (_isPurchase && variant.AvgCost > 0) Cost = variant.AvgCost.ToString("0.##", CultureInfo.InvariantCulture); });
        SaveCommand = new RelayCommand(Save);
        SearchCommand = new RelayCommand(Search);
        ManageSuppliersCommand = new RelayCommand(() => { Shell.CloseDialog(); Shell.Nav = "Suppliers"; });
    }

    public override double Width => 520;
    public ObservableCollection<VariantItem> Results { get; }
    public ICommand PickCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ManageSuppliersCommand { get; }
    public IReadOnlyList<SupplierItem> Suppliers { get; }
    public IReadOnlyList<StockLocationRow> Locations => Shell.S.Stock.Locations();
    public long LocationId { get; set; } = 1;
    public IReadOnlyList<StockLotRow> Lots => Selected is null ? [] : Shell.S.Stock.Lots(Selected.ProductId);
    public StockLotRow? Lot { get; set; }
    public bool HasShift => Shell.HasShift;
    public bool IsPurchase { get => _isPurchase; set { if (Set(ref _isPurchase, value)) { SetError(nameof(SelectedSupplier), null); SetError(nameof(Reason), null); Raise(nameof(IsAdjustment)); Raise(nameof(QtyLabelKey)); Raise(nameof(SaveLabel)); } } }
    public bool IsAdjustment { get => !_isPurchase; set => IsPurchase = !value; }
    public string QtyLabelKey => _isPurchase ? "Inv_QtyReceived" : "Inv_QtyHint";
    public string Query { get => _query; set { if (Set(ref _query, value)) Search(); } }
    public VariantItem? Selected { get => _selected; private set { if (Set(ref _selected, value)) { SetError(nameof(Selected), null); Raise(nameof(HasSelected)); Lot=null; Raise(nameof(Lots)); } } }
    public bool HasSelected => _selected is not null;
    public string Qty { get => _qty; set { if (Set(ref _qty, value)) SetError(nameof(Qty), null); } }
    public string Cost { get => _cost; set { if (Set(ref _cost, value)) SetError(nameof(Cost), null); } }
    public SupplierItem? SelectedSupplier { get => _selectedSupplier; set { if (Set(ref _selectedSupplier, value)) SetError(nameof(SelectedSupplier), null); } }
    public string SaveLabel => Localizer.Instance[_isPurchase ? "Inv_ContinueToInvoice" : "Action_Save"];
    public string Reference { get => _reference; set => Set(ref _reference, value); }
    public string Reason { get => _reason; set { if (Set(ref _reason, value)) SetError(nameof(Reason), null); } }
    public bool PaidFromRegister { get => _fromRegister; set => Set(ref _fromRegister, value); }

    void Search()
    {
        Results.Clear();
        var text = _query.Trim();
        if (text.Length == 0) return;
        var s = Shell.S;
        var hits = s.Catalog.FindByBarcode(text) is { } exact ? [exact] : s.Catalog.Search(text, 8).ToList();
        foreach (var h in hits)
        {
            var style = s.Styles.Get(h.StyleId);
            var row = s.Styles.Variants(h.StyleId).FirstOrDefault(v => v.ProductId == h.Id);
            if (style is null || row is null) continue;
            Results.Add(new VariantItem(row, style.Input.NameEn, style.Input.NameAlt, Enum.TryParse<GarmentKind>(style.Input.Kind, out var k) ? k : GarmentKind.TShirt));
        }
    }

    void Save()
    {
        var l = Localizer.Instance;
        SetError(nameof(Selected), _selected is null ? l["Err_Choose"] : null);
        var qtyOk = decimal.TryParse(_qty, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) && qty != 0 && (!_isPurchase || qty > 0);
        SetError(nameof(Qty), qtyOk ? null : l["Err_Qty"]);
        var costOk = decimal.TryParse(_cost, NumberStyles.Number, CultureInfo.InvariantCulture, out var cost) && cost >= 0;
        if (_isPurchase || qty > 0) SetError(nameof(Cost), costOk ? null : l["Err_Number"]);
        if (_isPurchase) SetError(nameof(SelectedSupplier), _selectedSupplier is null ? l["Err_Choose"] : null);
        if (!_isPurchase) SetError(nameof(Reason), string.IsNullOrWhiteSpace(_reason) ? l["Err_Required"] : null);
        if (_isPurchase && _fromRegister && !Shell.HasShift) { SetError(nameof(Qty), l["Toast_NeedShift"]); }
        if (HasErrors) return;
        try
        {
            var s = Shell.S;
            var user = Shell.User!.Id;
            if (_isPurchase)
            {
                // Review payment, credit and dates in the shared invoice flow; nothing is posted here.
                var invoice = new PurchaseInvoiceViewModel(Shell, _selectedSupplier!, _done) { SupplierRef = _reference.Trim() };
                invoice.PickCommand.Execute(_selected!);
                invoice.Lines[0].QtyText = qty.ToString(CultureInfo.InvariantCulture);
                invoice.Lines[0].CostText = _cost.Trim();
                invoice.Lines[0].LocationId = LocationId;
                if (_fromRegister) invoice.PayAllCommand.Execute(null);
                Shell.ShowDialog(invoice);
                return;
            }
            s.Stock.AdjustStock(Guid.NewGuid(),user,_selected!.ProductId,LocationId,Lot?.Id,qty,qty>0?cost:null,_reason.Trim());
            Shell.CloseDialog();
            _done();
            Shell.Refresh();
            Shell.Toast(l["Toast_MovementAdded"]);
        }
        catch (PosException ex) { SetError(nameof(Qty), Shell.ErrorText(ex)); }
    }
}
