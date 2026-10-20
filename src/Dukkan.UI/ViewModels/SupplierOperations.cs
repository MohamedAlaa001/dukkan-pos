using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace Dukkan.UI;

internal static class SupplierVariantLookup
{
    public static VariantItem? Get(ShellViewModel shell, long productId)
    {
        var product = shell.S.Catalog.GetById(productId);
        if (product is null) return null;
        var style = shell.S.Styles.Get(product.StyleId);
        var variant = shell.S.Styles.Variants(product.StyleId).FirstOrDefault(v => v.ProductId == productId);
        return style is null || variant is null ? null : new VariantItem(variant, style.Input.NameEn, style.Input.NameAlt,
            Enum.TryParse<GarmentKind>(style.Input.Kind, out var kind) ? kind : GarmentKind.TShirt);
    }
}

public sealed class PurchaseOrderItem(PurchaseOrderRow row) : LocalizedObject
{
    public PurchaseOrderRow Row { get; } = row;
    public long Id => Row.Id;
    public string Number => Row.Number;
    public string Supplier => Row.Supplier;
    public string Date => Row.ExpectedDate ?? "";
    public string Status => Localizer.Instance["PO_Status_" + Row.Status];
    public decimal Total => Row.Total;
    public decimal Received => Row.Received;
    public decimal Ordered => Row.Ordered;
    public bool Overdue => Row.Overdue;
}

public sealed class SupplierReturnItem(SupplierReturnRow row) : LocalizedObject
{
    public SupplierReturnRow Row { get; } = row;
    public string Number => Row.Number;
    public string Date => Row.Date;
    public string Reason => Localizer.Instance["SR_Reason_" + Row.Reason];
    public decimal Total => Row.Total;
    public bool CanReverse => !Row.Reversed;
    public string Status => Localizer.Instance[Row.Reversed ? "Sup_Reversed" : "SR_Posted"];
}

public sealed class SupplierOperationsViewModel : DialogViewModel
{
    readonly Action _changed;
    SupplierItem? _supplier;
    bool _onlyOpen = true;
    string _from = "", _to = "";
    public SupplierOperationsViewModel(ShellViewModel shell, long? supplierId = null, Action? changed = null) : base(shell)
    {
        _changed = changed ?? (() => { });
        Suppliers = new[] { new SupplierItem(new SupplierRow(0, L["PO_AllSuppliers"], null, null, null, true, 0, 0, 0, 0)) }
            .Concat(shell.S.Suppliers.List().Select(s => new SupplierItem(s))).ToList();
        _supplier = Suppliers.FirstOrDefault(s => s.Id == supplierId) ?? Suppliers.FirstOrDefault();
        NewOrderCommand = new RelayCommand(() => Shell.ShowDialog(new PurchaseInvoiceViewModel(Shell, Supplier!, Reopen, orderMode: true)), () => Supplier is { Id: > 0 });
        NewReturnCommand = new RelayCommand(() => Shell.ShowDialog(new SupplierReturnViewModel(Shell, Supplier!, Reopen)), () => Supplier is { Id: > 0 });
        OpenOrderCommand = new RelayCommand(p => Shell.ShowDialog(new PurchaseOrderDetailsViewModel(Shell, ((PurchaseOrderItem)p!).Row, Reopen)));
        ReverseReturnCommand = new RelayCommand(p =>
        {
            var item = (SupplierReturnItem)p!;
            Shell.ShowDialog(new SupplierReturnReverseViewModel(Shell,item.Row.Id,item.Number,Reopen));
        });
        ExportCommand = new RelayCommand(Export);
        Load();
    }
    public override double Width => 960;
    public Localizer L => Localizer.Instance;
    public IReadOnlyList<SupplierItem> Suppliers { get; }
    public SupplierItem? Supplier { get => _supplier; set { if (Set(ref _supplier, value)) Load(); } }
    public bool OnlyOpen { get => _onlyOpen; set { if (Set(ref _onlyOpen, value)) Load(); } }
    public string From { get => _from; set { if (Set(ref _from, value)) Load(); } }
    public string To { get => _to; set { if (Set(ref _to, value)) Load(); } }
    public decimal Credit { get; private set; }
    public decimal Purchases { get; private set; }
    public decimal ReturnsTotal { get; private set; }
    public decimal NetPurchases => Purchases - ReturnsTotal;
    public IReadOnlyList<PurchaseOrderItem> Orders { get; private set; } = [];
    public IReadOnlyList<SupplierReturnItem> Returns { get; private set; } = [];
    public IReadOnlyList<SupplierStatementItem> Statement { get; private set; } = [];
    int _tab;
    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public ICommand NewOrderCommand { get; }
    public ICommand NewReturnCommand { get; }
    public ICommand OpenOrderCommand { get; }
    public ICommand ReverseReturnCommand { get; }
    public ICommand ExportCommand { get; }
    void Reopen() { _changed(); Shell.Refresh(); Shell.ShowDialog(new SupplierOperationsViewModel(Shell, Supplier?.Id, _changed)); }
    void Load()
    {
        var s = Shell.S.Suppliers;
        var id = Supplier is { Id: > 0 } ? (long?)Supplier.Id : null;
        bool InRange(string date) => (_from.Length == 0 || string.CompareOrdinal(date, _from) >= 0) && (_to.Length == 0 || string.CompareOrdinal(date, _to) <= 0);
        Orders = s.OrderList(id, _onlyOpen).Select(o => new PurchaseOrderItem(o)).ToList();
        Returns = s.ReturnList(id).Where(r => InRange(r.Date)).Select(r => new SupplierReturnItem(r)).ToList();
        Credit = id is long i ? s.AvailableCredit(i) : Suppliers.Where(su => su.Id > 0).Sum(su => s.AvailableCredit(su.Id));
        Purchases = s.InvoiceList(id).Where(i => !i.Reversed && i.Kind == "purchase" && InRange(i.InvoiceDate)).Sum(i => i.Total);
        ReturnsTotal = Returns.Where(r => r.CanReverse).Sum(r => r.Total);
        Statement = id is long si ? s.Statement(si).Where(r => InRange(r.Date)).Select(r => new SupplierStatementItem(r)).ToList() : [];
        Raise(nameof(Orders)); Raise(nameof(Returns)); Raise(nameof(Credit)); Raise(nameof(Purchases)); Raise(nameof(ReturnsTotal)); Raise(nameof(NetPurchases)); Raise(nameof(Statement));
    }
    void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV|*.csv", FileName = "dukkan-supplier-trading.csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            CsvExport.Write(dialog.FileName, [L["Col_Supplier"], L["PO_Purchases"], L["SR_Returns"], L["PO_Net"]],
                new[] { new[] { Supplier?.Name ?? "", Purchases.ToString("0.00", CultureInfo.InvariantCulture), ReturnsTotal.ToString("0.00", CultureInfo.InvariantCulture), NetPurchases.ToString("0.00", CultureInfo.InvariantCulture) } });
            Shell.Toast(L["Toast_Exported"]);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Shell.Toast(L["Toast_CopyFailed"], BadgeKind.Danger); }
    }
}

public sealed class ReturnRestockChoice(long productId,string product,IReadOnlyList<StockLotRow> lots)
{
    public long ProductId {get;}=productId;
    public string Product {get;}=product;
    public IReadOnlyList<StockLotRow> Lots {get;}=lots;
    public StockLotRow? Lot {get;set;}
    public string Batch {get;set;}="";
    public string Expiry {get;set;}="";
}
public sealed class SupplierReturnReverseViewModel : DialogViewModel
{
    readonly long _returnId;readonly Action _done;readonly Guid _operation=Guid.NewGuid();
    string _reason="",_error="";
    public SupplierReturnReverseViewModel(ShellViewModel shell,long returnId,string number,Action done):base(shell)
    {
        _returnId=returnId;Number=number;_done=done;
        Rows=shell.S.Suppliers.ReturnRestockNeeds(returnId).Select(r=>new ReturnRestockChoice(r.ProductId,SupplierVariantLookup.Get(shell,r.ProductId)?.FullName??r.Product,shell.S.Stock.Lots(r.ProductId))).ToList();
        SaveCommand=new RelayCommand(Save);
    }
    public override double Width=>620;
    public string Number {get;}
    public IReadOnlyList<ReturnRestockChoice> Rows {get;}
    public bool NeedsLots=>Rows.Count>0;
    public string Reason {get=>_reason;set=>Set(ref _reason,value);}
    public string Error {get=>_error;private set=>Set(ref _error,value);}
    public ICommand SaveCommand {get;}
    void Save()
    {
        if(string.IsNullOrWhiteSpace(Reason)){Error=Localizer.Instance["Err_Required"];return;}
        try
        {
            var lots=new Dictionary<long,long>();
            foreach(var row in Rows)lots[row.ProductId]=row.Lot?.Id??Shell.S.Stock.DefineLot(Shell.User!.Id,row.ProductId,row.Batch,row.Expiry);
            Shell.S.Suppliers.ReverseReturn(_operation,Shell.User!.Id,_returnId,Reason,lots);Shell.CloseDialog();_done();
        }
        catch(PosException ex){Error=Shell.ErrorText(ex);}
    }
}

public sealed class PurchaseOrderDetailsViewModel : DialogViewModel
{
    readonly Action _changed;
    readonly Guid _send = Guid.NewGuid(), _cancel = Guid.NewGuid();
    public PurchaseOrderDetailsViewModel(ShellViewModel shell, PurchaseOrderRow row, Action changed) : base(shell)
    {
        Order = new(row); Lines = shell.S.Suppliers.OrderLines(row.Id); _changed = changed;
        SendCommand = new RelayCommand(() => Transition("open", null), () => row.Status == "draft");
        CancelOrderCommand = new RelayCommand(() => Shell.ShowDialog(new ReasonViewModel(Shell, L["PO_Cancel"], row.Number, reason => Transition("cancelled", reason))),
            () => row.Status is "draft" or "open" or "partial");
        ReceiveCommand = new RelayCommand(() => Shell.ShowDialog(new PurchaseInvoiceViewModel(Shell,
            new SupplierItem(Shell.S.Suppliers.Get(row.SupplierId)!), _changed, order: row)), () => row.Status is "open" or "partial");
        PrintCommand = new RelayCommand(() => Output(false));
        PdfCommand = new RelayCommand(() => Output(true));
    }
    public override double Width => 820;
    public Localizer L => Localizer.Instance;
    public PurchaseOrderItem Order { get; }
    public IReadOnlyList<PurchaseOrderLineRow> Lines { get; }
    public ICommand SendCommand { get; }
    public ICommand CancelOrderCommand { get; }
    public ICommand ReceiveCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand PdfCommand { get; }
    void Transition(string state, string? reason)
    {
        try { Shell.S.Suppliers.TransitionOrder(state == "open" ? _send : _cancel, Shell.User!.Id, Order.Id, state, reason); _changed(); }
        catch (PosException ex) { Shell.ToastError(ex); }
    }
    void Output(bool pdf)
    {
        try
        {
            var keys = new[] { "PO_Title", "Col_Supplier", "PO_Expected", "Col_Item", "Col_Qty", "Col_Cost", "Col_Total", "PO_NoPosting" };
            var pages = SupplierDocuments.PurchaseOrder(Order.Row, Lines, Shell.S.Settings.GetValue("store_name") ?? "Dukkan", L.IsArabic, keys.ToDictionary(k => k, k => L[k]));
            if (pdf)
            {
                var save = new Microsoft.Win32.SaveFileDialog { Filter = "PDF|*.pdf", FileName = Order.Number + ".pdf" };
                if (save.ShowDialog() == true) { PdfWriter.Save(save.FileName, pages); Shell.Toast(L["Toast_Exported"]); }
            }
            else
            {
                var printer = Shell.S.Settings.GetValue("printer_a4");
                if (string.IsNullOrWhiteSpace(printer)) { Shell.Toast(L["PO_SetPrinter"], BadgeKind.Warning); return; }
                Shell.S.Printer.Print(printer, pages, Order.Number);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException) { Shell.Toast(L["Toast_CopyFailed"], BadgeKind.Danger); }
    }
}

public sealed class SupplierReturnLineEdit : ObservableObject
{
    readonly Action _changed;
    string _qty = "0", _cost;
    public SupplierReturnLineEdit(long productId, string description, decimal maximum, decimal unitCost, long? invoiceLineId, Action changed)
    { ProductId = productId; Description = description; Maximum = maximum; InvoiceLineId = invoiceLineId; _cost = unitCost.ToString("0.####", CultureInfo.InvariantCulture); _changed = changed; }
    public long ProductId { get; }
    public long? InvoiceLineId { get; }
    public string Description { get; }
    public decimal Maximum { get; }
    public bool CostReadOnly => InvoiceLineId is not null;
    public string Quantity { get => _qty; set { if (Set(ref _qty, value)) _changed(); } }
    public string Cost { get => _cost; set { if (Set(ref _cost, value)) _changed(); } }
    public decimal Qty => decimal.TryParse(_qty, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : -1;
    public decimal UnitCost => decimal.TryParse(_cost, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : -1;
    public decimal PreviewCredit { get; init; }
    public IReadOnlyList<StockLocationRow> Locations { get; set; } = [];
    public IReadOnlyList<StockLotRow> Lots { get; set; } = [];
    public long LocationId { get; set; } = 1;
    public StockLotRow? Lot { get; set; }
}

public sealed class SupplierReturnViewModel : DialogViewModel
{
    readonly SupplierItem _supplier;
    readonly Action _changed;
    readonly Guid _operation = Guid.NewGuid();
    SupplierInvoiceItem? _invoice;
    Option _reason;
    string _date, _notes = "", _query = "", _error = "";
    bool _posting;
    public SupplierReturnViewModel(ShellViewModel shell, SupplierItem supplier, Action changed) : base(shell)
    {
        _supplier = supplier; _changed = changed; _date = shell.S.Queries.Today;
        Invoices = shell.S.Suppliers.InvoiceList(supplier.Id).Where(i => !i.Reversed && i.Kind == "purchase").Select(i => new SupplierInvoiceItem(i)).ToList();
        Reasons = new[] { "damaged", "wrong_item", "expired", "other" }.Select(k => new Option("SR_Reason_" + k)).ToList(); _reason = Reasons[0];
        PickCommand = new RelayCommand(p => Add((VariantItem)p!));
        ClearInvoiceCommand = new RelayCommand(() => { Invoice = null; Lines.Clear(); Recalc(); });
        SaveCommand = new RelayCommand(Save, () => !_posting);
        Invoice = Invoices.FirstOrDefault();
    }
    public override double Width => 800;
    public Localizer L => Localizer.Instance;
    public string SupplierName => _supplier.Name;
    public IReadOnlyList<SupplierInvoiceItem> Invoices { get; }
    public IReadOnlyList<Option> Reasons { get; }
    public SupplierInvoiceItem? Invoice { get => _invoice; set { if (Set(ref _invoice, value)) LoadLines(); } }
    public bool IsUnlinked => _invoice is null;
    public Option Reason { get => _reason; set => Set(ref _reason, value); }
    public string Date { get => _date; set => Set(ref _date, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string Query { get => _query; set { if (Set(ref _query, value)) Search(); } }
    public ObservableCollection<VariantItem> Results { get; } = [];
    public ObservableCollection<SupplierReturnLineEdit> Lines { get; } = [];
    public decimal Total => Lines.Where(l => l.Qty > 0 && l.UnitCost >= 0).Sum(l =>
    {
        if (l.InvoiceLineId is long id)
        {
            var source = Shell.S.Suppliers.PurchaseLines(Invoice!.Id).First(s => s.Id == id);
            var before = Money.ToCents(source.ReturnedCredit);
            var after = (long)decimal.Round(Money.ToCents(source.Total) * (source.Returned + l.Qty) / source.Quantity, 0, MidpointRounding.AwayFromZero);
            return Money.FromCents(Math.Clamp(after - before, 0, Money.ToCents(source.Total) - before));
        }
        return Money.Round(l.Qty * l.UnitCost);
    });
    public decimal BalanceAfter => _supplier.Balance - Total;
    public ICommand PickCommand { get; }
    public ICommand ClearInvoiceCommand { get; }
    public ICommand SaveCommand { get; }
    void Recalc() { Raise(nameof(Total)); Raise(nameof(BalanceAfter)); }
    void LoadLines()
    {
        Lines.Clear();
        if (_invoice is not null)
            foreach (var l in Shell.S.Suppliers.PurchaseLines(_invoice.Id).Where(l => l.Quantity > l.Returned))
                Lines.Add(Prepare(new(l.ProductId, l.Description, l.Quantity - l.Returned, l.UnitCost, l.Id, Recalc),l.LocationId,l.LotId));
        Raise(nameof(IsUnlinked)); Recalc();
    }
    void Search()
    {
        Results.Clear();
        if (_query.Trim().Length == 0 || !IsUnlinked) return;
        foreach (var hit in Shell.S.Catalog.Search(_query.Trim(), 6))
            if (SupplierVariantLookup.Get(Shell, hit.Id) is { } variant) Results.Add(variant);
    }
    void Add(VariantItem item)
    {
        if (Lines.Any(l => l.ProductId == item.ProductId)) return;
        Lines.Add(Prepare(new(item.ProductId, item.FullName, item.Stock, item.AvgCost, null, Recalc) { Quantity = "1" },1,null));
        Query = ""; Results.Clear(); Recalc();
    }
    SupplierReturnLineEdit Prepare(SupplierReturnLineEdit line,long location,long? lot)
    {
        line.Locations=Shell.S.Stock.Locations(); line.LocationId=location; line.Lots=Shell.S.Stock.Lots(line.ProductId);
        line.Lot=line.Lots.FirstOrDefault(l=>l.Id==lot); return line;
    }
    void Save()
    {
        if (_posting) return;
        if (!DateOnly.TryParseExact(_date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            Lines.Any(l => l.Qty < 0 || l.Qty > l.Maximum || l.UnitCost < 0) || !Lines.Any(l => l.Qty > 0) || IsUnlinked && string.IsNullOrWhiteSpace(_notes))
        { Error = L["SR_Invalid"]; return; }
        _posting = true;
        try
        {
            var result = Shell.S.Suppliers.PostReturn(new(_operation, Shell.User!.Id, _supplier.Id, _date, _reason.Key["SR_Reason_".Length..], _notes,
                _invoice?.Id, Lines.Where(l => l.Qty > 0).Select(l => new SupplierReturnLine(l.ProductId, l.Qty, l.UnitCost, l.InvoiceLineId,l.LocationId,l.Lot?.Id)).ToList()));
            Shell.CloseDialog(); _changed(); Shell.Refresh(); Shell.Toast(L["SR_Posted"] + " " + result.Number);
        }
        catch (PosException ex) { Error = Shell.ErrorText(ex); }
        finally { _posting = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
