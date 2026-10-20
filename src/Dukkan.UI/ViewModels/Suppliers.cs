using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

// ===================================================================== items
public sealed class SupplierItem : LocalizedObject
{
    public SupplierItem(SupplierRow r)
    {
        Id = r.Id; Name = r.Name; Phone = r.Phone ?? ""; Address = r.Address ?? ""; Notes = r.Notes ?? ""; Active = r.Active;
        Balance = r.Balance; TotalPurchases = r.TotalPurchases; TotalPaid = r.TotalPaid; Overdue = r.Overdue; Opening = r.Opening;
    }

    public long Id { get; }
    public string Name { get; }
    public string Phone { get; }
    public string Address { get; }
    public string Notes { get; }
    public bool Active { get; }
    public decimal Balance { get; }
    public decimal TotalPurchases { get; }
    public decimal TotalPaid { get; }
    public decimal Overdue { get; }
    public decimal Opening { get; }
    public bool HasOpening => Opening > 0;
    public bool HasOverdue => Overdue > 0;
    public string Status => Localizer.Instance[!Active ? "Status_Archived" : Balance > 0 ? "Sup_Owed" : Balance < 0 ? "SR_Credit" : "Sup_Settled"];
    public BadgeKind StatusKind => !Active ? BadgeKind.Neutral : Overdue > 0 ? BadgeKind.Danger : Balance > 0 ? BadgeKind.Warning : Balance < 0 ? BadgeKind.Info : BadgeKind.Success;
    public override string ToString() => Name;
}

public sealed class SupplierInvoiceItem : LocalizedObject
{
    public SupplierInvoiceItem(SupplierInvoiceRow r) => Row = r;
    public SupplierInvoiceRow Row { get; }
    public long Id => Row.Id;
    public string Supplier => Row.Supplier;
    public string Number => Row.Number;
    public string Reference => Row.Kind == "opening" ? Localizer.Instance["Sup_OpeningEntry"] : Row.SupplierRef ?? "";
    public string Date => Row.InvoiceDate;
    public string Due => Row.DueDate ?? "";
    public decimal Total => Row.Total;
    public decimal Paid => Row.Paid;
    public decimal Outstanding => Row.Outstanding;
    public bool IsPurchase => Row.Kind == "purchase";
    public bool CanReverse => !Row.Reversed;
    public string Status => Localizer.Instance[Row.Status switch { "unpaid" => "Sup_Unpaid", "partial" => "Sup_Partial", "paid" => "Sup_Paid", _ => "Sup_Reversed" }];
    public BadgeKind StatusKind => Row.Status switch { "paid" => BadgeKind.Success, "partial" => BadgeKind.Info, "unpaid" => BadgeKind.Warning, _ => BadgeKind.Neutral };
    public bool Overdue => Row.Overdue;
    public override string ToString() => Number;
    public string OverdueText => Localizer.Instance["Sup_Overdue"];
}

public sealed class SupplierPaymentItem : LocalizedObject
{
    public SupplierPaymentItem(SupplierPaymentRow r) => Row = r;
    public SupplierPaymentRow Row { get; }
    public long Id => Row.Id;
    public string Number => Row.Number;
    public string Date => Row.Date;
    public string MethodText => Row.MethodNameEn is null?Localizer.Instance["Pay_" + char.ToUpperInvariant(Row.Method[0]) + Row.Method[1..]]:Localizer.Instance.Pick(Row.MethodNameEn,Row.MethodNameAr??Row.MethodNameEn);
    public decimal Amount => Row.Amount;
    public string Allocated => Row.Allocated;
    public string Reference => Row.Reference ?? "";
    public bool CanReverse => !Row.Reversed;
    public string Status => Row.Reversed ? Localizer.Instance["Sup_Reversed"] : "";
    public bool Reversed => Row.Reversed;
}

public sealed class SupplierStatementItem : LocalizedObject
{
    public SupplierStatementItem(SupplierStatementRow r) => Row = r;
    public SupplierStatementRow Row { get; }
    public string Date => Row.Date;
    public string Document => Row.Document;
    public string Type => Localizer.Instance["Stmt_" + Row.Type];
    public decimal Amount => Row.Amount;
    public decimal Running => Row.Running;
}

// ===================================================================== page
public sealed class SuppliersViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    string _search = "";
    bool _showArchived;
    int _tab;
    List<SupplierItem> _all = [];
    SupplierDetailsViewModel? _details;
    Option _status;
    SupplierItem _filterSupplier;
    string _from = "", _to = "";

    public SuppliersViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Pager = new Pager<SupplierItem>(8);
        DuePager = new Pager<SupplierInvoiceItem>(8);
        StatusFilters = new[] { "Sup_FilterOutstanding", "Sup_FilterOverdue", "Sup_FilterUnpaid", "Sup_FilterPartial", "Sup_FilterPaid", "Sup_FilterAll" }.Select(k => new Option(k)).ToList();
        _status = StatusFilters[0];
        _filterSupplier = new SupplierItem(new SupplierRow(0, "", null, null, null, true, 0, 0, 0, 0));
        AddCommand = new RelayCommand(() => _shell.ShowDialog(new SupplierEditViewModel(_shell, null, Load)), () => _shell.IsOwner);
        OpenCommand = new RelayCommand(p => Open((SupplierItem)p!));
        BackCommand = new RelayCommand(() => { Details = null; Load(); });
        ExportCommand = new RelayCommand(Export);
        OperationsCommand = new RelayCommand(() => _shell.ShowDialog(new SupplierOperationsViewModel(_shell, changed: Load)), () => _shell.IsOwner);
        Load();
    }

    public Localizer L => Localizer.Instance;
    public Pager<SupplierItem> Pager { get; }
    public Pager<SupplierInvoiceItem> DuePager { get; }
    public IReadOnlyList<Option> StatusFilters { get; }
    public ObservableCollection<SupplierItem> FilterSuppliers { get; } = [];
    public ICommand AddCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OperationsCommand { get; }
    public bool IsOwner => _shell.IsOwner;
    public int Tab { get => _tab; set { if (Set(ref _tab, value)) LoadDue(); } }
    public string Search { get => _search; set { if (Set(ref _search, value)) Load(); } }
    public bool ShowArchived { get => _showArchived; set { if (Set(ref _showArchived, value)) Load(); } }
    public Option StatusFilter { get => _status; set { if (Set(ref _status, value)) LoadDue(); } }
    public SupplierItem FilterSupplier { get => _filterSupplier; set { if (value is not null && Set(ref _filterSupplier, value)) LoadDue(); } }
    public string From { get => _from; set { if (Set(ref _from, value)) LoadDue(); } }
    public string To { get => _to; set { if (Set(ref _to, value)) LoadDue(); } }
    public SupplierDetailsViewModel? Details { get => _details; private set { if (Set(ref _details, value)) { Raise(nameof(IsListVisible)); Raise(nameof(IsDetailsVisible)); } } }
    public bool IsListVisible => _details is null;
    public bool IsDetailsVisible => _details is not null;
    public decimal TotalOwed => _all.Where(s => s.Active).Sum(s => Math.Max(0, s.Balance));
    public decimal DueToday { get; private set; }
    public decimal OverdueTotal { get; private set; }
    public int SupplierCount => _all.Count(s => s.Active);

    void Load()
    {
        _all = _shell.S.Suppliers.List(null, true).Select(r => new SupplierItem(r)).ToList();
        var shown = _all.Where(s => (_showArchived || s.Active)
            && (string.IsNullOrWhiteSpace(_search) || s.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase) || s.Phone.Contains(_search, StringComparison.Ordinal))).ToList();
        Pager.SetSource(shown);
        var selectedSupplierId = _filterSupplier.Id;
        FilterSuppliers.Clear();
        FilterSuppliers.Add(new SupplierItem(new SupplierRow(0, "", null, null, null, true, 0, 0, 0, 0)));
        foreach (var s in _all) FilterSuppliers.Add(s);
        FilterSupplier = FilterSuppliers.FirstOrDefault(s => s.Id == selectedSupplierId) ?? FilterSuppliers[0];
        var due = _shell.S.Suppliers.DueSummary();
        DueToday = due.DueToday; OverdueTotal = due.Overdue;
        Raise(nameof(TotalOwed)); Raise(nameof(DueToday)); Raise(nameof(OverdueTotal)); Raise(nameof(SupplierCount));
        LoadDue();
    }

    void LoadDue()
    {
        var status = _status.Key switch
        {
            "Sup_FilterOutstanding" => "outstanding", "Sup_FilterOverdue" => "overdue", "Sup_FilterUnpaid" => "unpaid",
            "Sup_FilterPartial" => "partial", "Sup_FilterPaid" => "paid", _ => null,
        };
        string? Date(string s) => DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? s.Trim() : null;
        var rows = _shell.S.Suppliers.InvoiceList(_filterSupplier.Id > 0 ? _filterSupplier.Id : null, Date(_from), Date(_to), status)
            .Where(r => r.Kind == "purchase" || r.Kind == "opening").OrderBy(r => r.DueDate ?? "9999-12-31", StringComparer.Ordinal).ToList();
        DuePager.SetSource(rows.Select(r => new SupplierInvoiceItem(r)).ToList());
    }

    void Open(SupplierItem s) => Details = new SupplierDetailsViewModel(_shell, s.Id, () => { Load(); });

    /// <summary>Supplier balances report (current owed per supplier) as CSV.</summary>
    void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV|*.csv", FileName = $"dukkan-supplier-balances-{_shell.S.Queries.Today}.csv" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            CsvExport.Write(dlg.FileName, ["Supplier", "Phone", "Total purchases", "Total paid", "Outstanding", "Overdue"],
                _all.Where(s => s.Active).Select(s => new[] { s.Name, s.Phone, s.TotalPurchases.ToString("0.00", CultureInfo.InvariantCulture),
                    s.TotalPaid.ToString("0.00", CultureInfo.InvariantCulture), s.Balance.ToString("0.00", CultureInfo.InvariantCulture), s.Overdue.ToString("0.00", CultureInfo.InvariantCulture) }));
            _shell.Toast(Localizer.Instance["Toast_Exported"]);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _shell.Toast(Localizer.Instance["Toast_CopyFailed"], BadgeKind.Danger);
        }
    }
}

public sealed class SupplierDetailsViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    readonly long _id;
    readonly Action _changed;
    SupplierItem _supplier = null!;

    public SupplierDetailsViewModel(ShellViewModel shell, long supplierId, Action changed)
    {
        _shell = shell; _id = supplierId; _changed = changed;
        PurchaseCommand = new RelayCommand(() => _shell.ShowDialog(new PurchaseInvoiceViewModel(_shell, _supplier, Reload)), () => _shell.IsOwner && _supplier.Active);
        PayCommand = new RelayCommand(() => _shell.ShowDialog(new SupplierPayViewModel(_shell, _supplier, Reload)), () => _shell.IsOwner && _supplier.Balance > 0);
        OpeningCommand = new RelayCommand(() => _shell.ShowDialog(new SupplierOpeningViewModel(_shell, _supplier, Reload)), () => _shell.IsOwner && _supplier.Active);
        EditCommand = new RelayCommand(() => _shell.ShowDialog(new SupplierEditViewModel(_shell, _supplier, Reload)), () => _shell.IsOwner);
        ArchiveCommand = new RelayCommand(ToggleArchive, () => _shell.IsOwner);
        OperationsCommand = new RelayCommand(() => _shell.ShowDialog(new SupplierOperationsViewModel(_shell, _id, Reload)), () => _shell.IsOwner);
        ReturnCommand = new RelayCommand(() => _shell.ShowDialog(new SupplierReturnViewModel(_shell, _supplier, Reload)), () => _shell.IsOwner && _supplier.Active);
        ViewInvoiceCommand = new RelayCommand(p => _shell.ShowDialog(new SupplierInvoiceViewModel(_shell, ((SupplierInvoiceItem)p!).Row)));
        ReverseInvoiceCommand = new RelayCommand(p => ReverseInvoice((SupplierInvoiceItem)p!), p => _shell.IsOwner);
        ReversePaymentCommand = new RelayCommand(p => ReversePayment((SupplierPaymentItem)p!), p => _shell.IsOwner);
        Localizer.Instance.PropertyChanged += (_, _) => Raise(nameof(ArchiveText));
        Reload();
    }

    public Localizer L => Localizer.Instance;
    public SupplierItem Supplier { get => _supplier; private set => Set(ref _supplier, value); }
    public IReadOnlyList<SupplierInvoiceItem> Invoices { get; private set; } = [];
    public IReadOnlyList<SupplierPaymentItem> Payments { get; private set; } = [];
    public IReadOnlyList<SupplierStatementItem> Statement { get; private set; } = [];
    public IReadOnlyList<PurchaseOrderItem> Orders { get; private set; } = [];
    public IReadOnlyList<SupplierReturnItem> Returns { get; private set; } = [];
    public ICommand OpenOrderCommand => new RelayCommand(p => _shell.ShowDialog(new PurchaseOrderDetailsViewModel(_shell, ((PurchaseOrderItem)p!).Row, Reload)));
    public ICommand ReverseReturnCommand => new RelayCommand(p =>
    {
        var item = (SupplierReturnItem)p!;
        _shell.ShowDialog(new ReasonViewModel(_shell, L["SR_Reverse"], item.Number, reason =>
        {
            try { _shell.S.Suppliers.ReverseReturn(Guid.NewGuid(), _shell.User!.Id, item.Row.Id, reason); _shell.Refresh(); Reload(); }
            catch (PosException ex) { _shell.ToastError(ex); }
        }));
    });
    public bool IsOwner => _shell.IsOwner;
    public ICommand PurchaseCommand { get; }
    public ICommand PayCommand { get; }
    public ICommand OpeningCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand OperationsCommand { get; }
    public ICommand ReturnCommand { get; }
    public ICommand ViewInvoiceCommand { get; }
    public ICommand ReverseInvoiceCommand { get; }
    public ICommand ReversePaymentCommand { get; }
    public string ArchiveText => L[_supplier.Active ? "Action_Archive" : "Action_Restore"];

    void Reload()
    {
        var s = _shell.S.Suppliers;
        Supplier = new SupplierItem(s.Get(_id)!);
        Invoices = s.InvoiceList(_id).Select(r => new SupplierInvoiceItem(r)).ToList();
        Payments = s.Payments(_id).Select(r => new SupplierPaymentItem(r)).ToList();
        Statement = s.Statement(_id).Select(r => new SupplierStatementItem(r)).Reverse().ToList();
        Orders = s.OrderList(_id).Select(r => new PurchaseOrderItem(r)).ToList();
        Returns = s.ReturnList(_id).Select(r => new SupplierReturnItem(r)).ToList();
        Raise(nameof(Orders)); Raise(nameof(Returns));
        Raise(nameof(Invoices)); Raise(nameof(Payments)); Raise(nameof(Statement)); Raise(nameof(ArchiveText));
        CommandManager.InvalidateRequerySuggested();
        _changed();
    }

    void ToggleArchive()
    {
        try
        {
            _shell.S.Suppliers.SetActive(_shell.User!.Id, _id, !_supplier.Active);
            Reload();
            _shell.Toast(L[_supplier.Active ? "Toast_Saved" : "Toast_Archived"]);
        }
        catch (PosException ex) { _shell.ToastError(ex); }
    }

    void ReverseInvoice(SupplierInvoiceItem i) =>
        _shell.ShowDialog(new ReasonViewModel(_shell, L["Sup_ReversePurchase"], $"{i.Number} · {L.Money(i.Total)}", reason =>
        {
            try { _shell.S.Suppliers.ReversePurchase(_shell.User!.Id, i.Id, reason); _shell.Refresh(); Reload(); _shell.Toast(L["Toast_Saved"]); }
            catch (PosException ex) { _shell.ToastError(ex); }
        }));

    void ReversePayment(SupplierPaymentItem p) =>
        _shell.ShowDialog(new ReasonViewModel(_shell, L["Sup_ReversePayment"], $"{p.Number} · {L.Money(p.Amount)}", reason =>
        {
            try
            {
                _shell.S.Suppliers.ReversePayment(_shell.User!.Id, p.Id, reason, _shell.Shift?.Id);
                _shell.Refresh(); Reload(); _shell.Toast(L["Toast_Saved"]);
            }
            catch (PosException ex) { _shell.ToastError(ex); }
        }));
}

// ===================================================================== dialogs
/// <summary>Asks for a reason (corrections and reversals always need one) and passes it to the callback.</summary>
public sealed class ReasonViewModel : DialogViewModel
{
    readonly Action<string> _done;
    string _reason = "";

    public ReasonViewModel(ShellViewModel shell, string title, string subject, Action<string> done) : base(shell)
    {
        Title = title; Subject = subject; _done = done;
    }

    public override double Width => 440;
    public string Title { get; }
    public string Subject { get; }
    public string Reason { get => _reason; set { if (Set(ref _reason, value)) SetError(nameof(Reason), null); } }
    public ICommand ConfirmCommand => new RelayCommand(() =>
    {
        if (string.IsNullOrWhiteSpace(_reason)) { SetError(nameof(Reason), Localizer.Instance["Err_Required"]); return; }
        Shell.CloseDialog();
        _done(_reason.Trim());
    });
}

public sealed class SupplierEditViewModel : DialogViewModel
{
    readonly SupplierItem? _existing;
    readonly Action _saved;
    string _name, _phone, _address, _notes;

    public SupplierEditViewModel(ShellViewModel shell, SupplierItem? existing, Action saved) : base(shell)
    {
        _existing = existing; _saved = saved;
        _name = existing?.Name ?? ""; _phone = existing?.Phone ?? ""; _address = existing?.Address ?? ""; _notes = existing?.Notes ?? "";
    }

    public override double Width => 480;
    public bool IsNew => _existing is null;
    public string Title => Localizer.Instance[IsNew ? "Sup_Add" : "Action_Edit"];
    public string Name { get => _name; set { if (Set(ref _name, value)) SetError(nameof(Name), null); } }
    public string Phone { get => _phone; set => Set(ref _phone, value); }
    public string Address { get => _address; set => Set(ref _address, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public ICommand SaveCommand => new RelayCommand(Save);

    void Save()
    {
        var l = Localizer.Instance;
        SetError(nameof(Name), string.IsNullOrWhiteSpace(_name) ? l["Err_Name"] : null);
        if (HasErrors) return;
        try
        {
            string? N(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            if (_existing is null) Shell.S.Suppliers.Create(Shell.User!.Id, _name.Trim(), N(_phone), N(_address), N(_notes));
            else Shell.S.Suppliers.Update(Shell.User!.Id, _existing.Id, _name.Trim(), N(_phone), N(_address), N(_notes));
            Shell.CloseDialog();
            _saved();
            Shell.Toast(l["Toast_Saved"]);
        }
        catch (PosException ex) { SetError(nameof(Name), Shell.ErrorText(ex)); }
    }
}

public sealed class SupplierOpeningViewModel : DialogViewModel
{
    readonly SupplierItem _supplier;
    readonly Action _saved;
    readonly Guid _op = Guid.NewGuid();
    string _amount = "", _due = "", _note = "";

    public SupplierOpeningViewModel(ShellViewModel shell, SupplierItem supplier, Action saved) : base(shell) { _supplier = supplier; _saved = saved; }

    public override double Width => 460;
    public string SupplierName => _supplier.Name;
    public string Amount { get => _amount; set { if (Set(ref _amount, value)) SetError(nameof(Amount), null); } }
    public string DueDate { get => _due; set { if (Set(ref _due, value)) SetError(nameof(DueDate), null); } }
    public string Note { get => _note; set => Set(ref _note, value); }
    public ICommand SaveCommand => new RelayCommand(Save);

    void Save()
    {
        var l = Localizer.Instance;
        var ok = decimal.TryParse(_amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0;
        SetError(nameof(Amount), ok ? null : l["Err_Number"]);
        SetError(nameof(DueDate), _due.Length == 0 || DateOnly.TryParseExact(_due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : l["Credit_BadDate"]);
        if (HasErrors) return;
        try
        {
            Shell.S.Suppliers.AddOpeningBalance(_op, Shell.User!.Id, _supplier.Id, v, _due.Length == 0 ? null : _due, string.IsNullOrWhiteSpace(_note) ? null : _note.Trim());
            Shell.CloseDialog();
            _saved();
            Shell.Toast(l["Toast_Saved"]);
        }
        catch (PosException ex) { SetError(nameof(Amount), Shell.ErrorText(ex)); }
    }
}

public sealed class PurchaseLineEdit : ObservableObject
{
    readonly Action _changed;
    string _qty = "1", _cost;

    public PurchaseLineEdit(VariantItem v, Action changed)
    {
        Variant = v; _changed = changed;
        _cost = v.AvgCost > 0 ? v.AvgCost.ToString("0.####", CultureInfo.InvariantCulture) : "";
    }

    public VariantItem Variant { get; }
    public string QtyText { get => _qty; set { if (Set(ref _qty, value)) { Raise(nameof(Total)); _changed(); } } }
    public string CostText { get => _cost; set { if (Set(ref _cost, value)) { Raise(nameof(Total)); _changed(); } } }
    public decimal Qty => decimal.TryParse(_qty, NumberStyles.Number, CultureInfo.InvariantCulture, out var q) ? q : 0;
    public long? OrderLineId { get; set; }
    public long LocationId { get; set; } = 1;
    public IReadOnlyList<StockLocationRow> Locations { get; set; } = [];
    public bool ShowStockFields { get; set; }
    public bool TrackExpiry { get; set; }
    public string Batch { get; set; } = "";
    public string Expiry { get; set; } = "";
    public decimal Cost => decimal.TryParse(_cost, NumberStyles.Number, CultureInfo.InvariantCulture, out var c) ? c : -1m;
    public decimal Total => Qty > 0 && Cost >= 0 ? Money.FromCents(Money.ToCents(Qty * Cost)) : 0m;
}

/// <summary>Purchase invoice entry: goods received from a supplier, with an optional payment made now.</summary>
public sealed class PurchaseInvoiceViewModel : DialogViewModel
{
    readonly SupplierItem _supplier;
    readonly Action _saved;
    readonly Guid _op = Guid.NewGuid();
    string _query = "", _ref = "", _date, _due = "", _paid = "0", _payRef = "", _notes = "", _error = "";
    PaymentMethodOption _method;
    bool _posting;
    readonly bool _orderMode;
    readonly long? _orderId;
    bool _confirmOverReceipt;

    public PurchaseInvoiceViewModel(ShellViewModel shell, SupplierItem supplier, Action saved, bool orderMode = false, PurchaseOrderRow? order = null) : base(shell)
    {
        _supplier = supplier; _saved = saved;
        _orderMode = orderMode; _orderId = order?.Id;
        _date = shell.S.Queries.Today;
        Methods = PaymentChoiceLists.Incoming(shell.S);
        _method = PaymentChoiceLists.First(Methods);
        PickCommand = new RelayCommand(p => AddLine((VariantItem)p!));
        RemoveCommand = new RelayCommand(p => { Lines.Remove((PurchaseLineEdit)p!); Recalc(); });
        PayAllCommand = new RelayCommand(() => Paid = Math.Max(0, Total - CreditApplied).ToString("0.##", CultureInfo.InvariantCulture));
        SaveCommand = new RelayCommand(Save, () => !_posting);
        if (order is not null)
        {
            _ref = order.Number;
            foreach (var line in shell.S.Suppliers.OrderLines(order.Id).Where(l => l.Remaining > 0))
                if (SupplierVariantLookup.Get(shell, line.ProductId) is { } variant)
                    Lines.Add(Prepare(new PurchaseLineEdit(variant, Recalc) { QtyText = line.Remaining.ToString(CultureInfo.InvariantCulture), CostText = line.UnitCost.ToString("0.####", CultureInfo.InvariantCulture), OrderLineId = line.Id }));
        }
    }

    public override double Width => 720;
    public string SupplierName => _supplier.Name;
    public string Title => Localizer.Instance[_orderMode ? "PO_New" : _orderId is null ? "Sup_NewPurchase" : "PO_Receive"];
    public string SaveText => Localizer.Instance[_orderMode ? "PO_SaveDraft" : "Sup_PostPurchase"];
    public string LinePrompt => Localizer.Instance[_orderMode ? "PO_AddItems" : "Sup_AddItem"];
    public bool IsInvoice => !_orderMode;
    public bool IsOrderReceipt => _orderId is not null;
    public bool CanAddItems => _orderId is null;
    public bool ConfirmOverReceipt { get => _confirmOverReceipt; set => Set(ref _confirmOverReceipt, value); }
    public decimal CreditApplied => Math.Min(Total, Shell.S.Suppliers.AvailableCredit(_supplier.Id));
    public ObservableCollection<VariantItem> Results { get; } = [];
    public ObservableCollection<PurchaseLineEdit> Lines { get; } = [];
    public IReadOnlyList<PaymentMethodOption> Methods { get; }
    public ManualCardEntry Card {get;}=new();
    public bool IsCard=>IsInvoice && Method.Kind=="card";
    public ICommand PickCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand PayAllCommand { get; }
    public ICommand SaveCommand { get; }
    public bool HasShift => Shell.HasShift;
    public string Query { get => _query; set { if (Set(ref _query, value)) Search(); } }
    public string SupplierRef { get => _ref; set => Set(ref _ref, value); }
    public string InvoiceDate { get => _date; set { if (Set(ref _date, value)) SetError(nameof(InvoiceDate), null); } }
    public string DueDate { get => _due; set { if (Set(ref _due, value)) SetError(nameof(DueDate), null); } }
    public string Paid { get => _paid; set { if (Set(ref _paid, value)) { Card.Confirmed = false; SetError(nameof(Paid), null); Recalc(); } } }
    public PaymentMethodOption Method { get => _method; set { if (Set(ref _method, value)){Raise(nameof(IsCash));Raise(nameof(IsCard));Card.Confirmed=false;} } }
    public bool IsCash => _method.Kind == "cash";
    public string PaymentReference { get => _payRef; set => Set(ref _payRef, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public string ErrorMessage { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;
    public decimal Total => Lines.Sum(l => l.Total);
    public decimal PaidValue => decimal.TryParse(_paid, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    public decimal Remaining => Math.Max(0, Total - CreditApplied - PaidValue);

    void Recalc() { Raise(nameof(Total)); Raise(nameof(Remaining)); Raise(nameof(CreditApplied)); }

    void AddLine(VariantItem v)
    {
        var existing = Lines.FirstOrDefault(l => l.Variant.ProductId == v.ProductId);
        if (existing is not null) existing.QtyText = (existing.Qty + 1).ToString(CultureInfo.InvariantCulture);
        else { var line = Prepare(new PurchaseLineEdit(v, Recalc)); Lines.Add(line); }
        Query = "";
        Results.Clear();
        Recalc();
    }

    PurchaseLineEdit Prepare(PurchaseLineEdit line)
    {
        line.Locations=Shell.S.Stock.Locations(); line.ShowStockFields=IsInvoice;
        line.TrackExpiry=Shell.S.Stock.ProductConfig(line.Variant.ProductId).TrackExpiry; return line;
    }

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
        bool Date(string s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        SetError(nameof(InvoiceDate), _orderMode || Date(_date) ? null : l["Credit_BadDate"]);
        SetError(nameof(DueDate), _due.Length == 0 || Date(_due) ? null : l["Credit_BadDate"]);
        var paidOk = decimal.TryParse(_paid, NumberStyles.Number, CultureInfo.InvariantCulture, out var paid) && paid >= 0 && paid <= Total - CreditApplied;
        SetError(nameof(Paid), paidOk ? null : l["Sup_PaidTooMuch"]);
        if (Lines.Count == 0) { ErrorMessage = l["Sup_NoLines"]; return; }
        if (Lines.Any(x => x.Qty <= 0 || x.Cost < 0)) { ErrorMessage = l["Err_Qty"]; return; }
        if (HasErrors) return;
        _posting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (_orderMode)
            {
                Shell.S.Suppliers.CreateOrder(_op, Shell.User!.Id, _supplier.Id, Lines.Select(x => new PurchaseLine(x.Variant.ProductId, x.Qty, x.Cost)).ToList(),
                    _due.Length == 0 ? null : _due, _notes);
                Shell.CloseDialog(); _saved(); Shell.Refresh(); Shell.Toast(l["Toast_Saved"]); return;
            }
            var req = new PostPurchaseRequest(_op, Shell.User!.Id, _supplier.Id, string.IsNullOrWhiteSpace(_ref) ? null : _ref.Trim(), _date,
                _due.Length == 0 ? null : _due, Lines.Select(x => new PurchaseLine(x.Variant.ProductId, x.Qty, x.Cost, OrderLineId: x.OrderLineId, LocationId:x.LocationId, Batch:x.Batch, Expiry:x.Expiry)).ToList(), paid,
                _method.MethodKind,
                Shell.Shift?.Id, string.IsNullOrWhiteSpace(_notes) ? null : _notes.Trim(), string.IsNullOrWhiteSpace(_payRef) ? null : _payRef.Trim(), _orderId, _confirmOverReceipt,_method.Id,IsCard?Card.Confirmation:null);
            var result = Shell.S.Suppliers.PostPurchase(req);
            Shell.CloseDialog();
            _saved();
            Shell.Refresh();
            Shell.Toast($"{l["Sup_PurchasePosted"]} ({result.Number})");
        }
        catch (PosException ex) { ErrorMessage = Shell.ErrorText(ex); }
        finally { _posting = false; CommandManager.InvalidateRequerySuggested(); }
    }
}

public sealed class SupplierAllocationEdit : ObservableObject
{
    readonly Action _changed;
    string _amount;

    public SupplierAllocationEdit(SupplierInvoiceRow inv, decimal amount, Action changed)
    {
        Invoice = inv; _changed = changed; _amount = amount.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public SupplierInvoiceRow Invoice { get; }
    public string Number => Invoice.Number;
    public string Due => Invoice.DueDate ?? "";
    public decimal Outstanding => Invoice.Outstanding;
    public bool Overdue => Invoice.Overdue;
    public string AmountText { get => _amount; set { if (Set(ref _amount, value)) _changed(); } }
    public decimal Amount => decimal.TryParse(_amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : -1m;
    public void Set(decimal v) { _amount = v.ToString("0.##", CultureInfo.InvariantCulture); Raise(nameof(AmountText)); }
}

/// <summary>Pay a supplier: the payment is split across outstanding invoices (oldest first by default; adjustable).</summary>
public sealed class SupplierPayViewModel : DialogViewModel
{
    readonly SupplierItem _supplier;
    readonly Action _saved;
    readonly Guid _op = Guid.NewGuid();
    string _amount = "", _date, _ref = "", _notes = "", _error = "";
    PaymentMethodOption _method;
    bool _updating, _posting;

    public SupplierPayViewModel(ShellViewModel shell, SupplierItem supplier, Action saved) : base(shell)
    {
        _supplier = supplier; _saved = saved;
        _date = shell.S.Queries.Today;
        Methods = PaymentChoiceLists.Incoming(shell.S);
        _method = PaymentChoiceLists.First(Methods);
        foreach (var i in shell.S.Suppliers.InvoiceList(supplier.Id, status: "outstanding").OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id))
            Rows.Add(new SupplierAllocationEdit(i, 0m, OnRowChanged));
        _amount = supplier.Balance.ToString("0.##", CultureInfo.InvariantCulture);
        Distribute();
        SaveCommand = new RelayCommand(Save, () => !_posting);
    }

    public override double Width => 600;
    public string SupplierName => _supplier.Name;
    public decimal Owed => _supplier.Balance;
    public ObservableCollection<SupplierAllocationEdit> Rows { get; } = [];
    public IReadOnlyList<PaymentMethodOption> Methods { get; }
    public ManualCardEntry Card {get;}=new();
    public bool IsCard=>Method.Kind=="card";
    public ICommand SaveCommand { get; }
    public bool HasShift => Shell.HasShift;
    public string Amount { get => _amount; set { if (Set(ref _amount, value)) { Card.Confirmed = false; SetError(nameof(Amount), null); Distribute(); } } }
    public string PayDate { get => _date; set { if (Set(ref _date, value)) SetError(nameof(PayDate), null); } }
    public PaymentMethodOption Method { get => _method; set {if(Set(ref _method,value)){Raise(nameof(IsCard));Card.Confirmed=false;}} }
    public string Reference { get => _ref; set => Set(ref _ref, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public string ErrorMessage { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;
    public decimal AllocatedTotal => Rows.Sum(r => Math.Max(0, r.Amount));
    public decimal AmountValue => decimal.TryParse(_amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    public decimal Difference => AmountValue - AllocatedTotal;

    /// <summary>Default: oldest outstanding invoice first.</summary>
    void Distribute()
    {
        _updating = true;
        var left = Money.ToCents(Math.Max(0, AmountValue));
        foreach (var r in Rows)
        {
            var take = Math.Min(left, Money.ToCents(r.Outstanding));
            r.Set(Money.FromCents(take));
            left -= take;
        }
        _updating = false;
        Raise(nameof(AllocatedTotal)); Raise(nameof(Difference));
    }

    void OnRowChanged() { if (!_updating) { Raise(nameof(AllocatedTotal)); Raise(nameof(Difference)); } }

    void Save()
    {
        var l = Localizer.Instance;
        SetError(nameof(PayDate), DateOnly.TryParseExact(_date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : l["Credit_BadDate"]);
        var v = AmountValue;
        SetError(nameof(Amount), v <= 0 ? l["Err_Number"] : v > Owed ? l["Sup_PayTooMuch"] : null);
        if (v > 0 && Money.ToCents(AllocatedTotal) != Money.ToCents(v)) SetError(nameof(Amount), string.Format(l["Sup_AllocSum"], l.Money(v), l.Money(AllocatedTotal)));
        if (HasErrors) return;
        _posting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var alloc = Rows.Where(r => r.Amount > 0).Select(r => new SupplierAllocation(r.Invoice.Id, r.Amount)).ToList();
            var res = Shell.S.Suppliers.Pay(new SupplierPayRequest(_op, Shell.User!.Id, _supplier.Id, _date, v,
                _method.MethodKind,
                string.IsNullOrWhiteSpace(_ref) ? null : _ref.Trim(), string.IsNullOrWhiteSpace(_notes) ? null : _notes.Trim(), Shell.Shift?.Id, alloc,_method.Id,IsCard?Card.Confirmation:null));
            Shell.CloseDialog();
            _saved();
            Shell.Refresh();
            Shell.Toast($"{l["Sup_PaymentPosted"]} ({res.Number})");
        }
        catch (PosException ex) { ErrorMessage = Shell.ErrorText(ex); }
        finally { _posting = false; CommandManager.InvalidateRequerySuggested(); }
    }
}

public sealed class SupplierInvoiceViewModel : DialogViewModel
{
    public SupplierInvoiceViewModel(ShellViewModel shell, SupplierInvoiceRow row) : base(shell)
    {
        Invoice = new SupplierInvoiceItem(row);
        Lines = shell.S.Suppliers.InvoiceLines(row.Id);
    }

    public override double Width => 600;
    public SupplierInvoiceItem Invoice { get; }
    public IReadOnlyList<SupplierInvoiceLineRow> Lines { get; }
    public bool HasLines => Lines.Count > 0;
}
