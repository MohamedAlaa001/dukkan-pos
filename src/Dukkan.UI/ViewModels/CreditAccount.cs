using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

// ===================================================================== items
public sealed class InstallmentItem : LocalizedObject
{
    public InstallmentItem(InstallmentRow r) => Row = r;
    public InstallmentRow Row { get; }
    public long SaleId => Row.SaleId;
    public string Invoice => Row.SaleNumber;
    public int Seq => Row.Seq;
    public string DueDate => Row.DueDate;
    public decimal Amount => Row.Amount;
    public decimal Paid => Row.Paid;
    public decimal Remaining => Row.Remaining;
    public bool Overdue => Row.Overdue;
    public bool CanReschedule => Row.Remaining > 0;
    public string Status => Localizer.Instance["Inst_" + Row.Status];
    public BadgeKind StatusKind => Row.Status switch { "paid" => BadgeKind.Success, "partial" => BadgeKind.Info, "due" => BadgeKind.Warning, _ => BadgeKind.Neutral };
}

public sealed class CustomerStatementItem : LocalizedObject
{
    public CustomerStatementItem(StatementEntry e) => Entry = e;
    public StatementEntry Entry { get; }
    public string Date => Entry.At.Length >= 10 ? Entry.At[..10] : Entry.At;
    public string Document => Entry.Document ?? "";
    public string Type => Localizer.Instance["Stmt_" + Entry.Type];
    public decimal Amount => Entry.Amount;
    public decimal Running => Entry.Running;
}

public sealed class CollectionItem : LocalizedObject
{
    public CollectionItem(CollectionRow r) => Row = r;
    public CollectionRow Row { get; }
    public long Id => Row.Id;
    public string Number => Row.Number;
    public string Date => Row.PayDate;
    public string MethodText => Row.MethodNameEn is null?Localizer.Instance["Pay_" + char.ToUpperInvariant(Row.Method[0]) + Row.Method[1..]]:Localizer.Instance.Pick(Row.MethodNameEn,Row.MethodNameAr??Row.MethodNameEn);
    public decimal Amount => Row.Amount;
    public string Allocated => Row.Allocated;
    public bool Reversed => Row.Reversed;
    public bool CanReverse => !Row.Reversed;
}

public sealed class DueItemView : LocalizedObject
{
    public DueItemView(DueItem d) => Item = d;
    public DueItem Item { get; }
    public string Customer => Item.Customer;
    public string Document => Item.Document;
    public string Due => Item.DueDate ?? "";
    public decimal Remaining => Item.Remaining;
    public bool Overdue => Item.Overdue;
    public string Status => Localizer.Instance[Item.DueDate is null ? "Inst_open" : "Inst_" + Item.Status];
    public BadgeKind StatusKind => Item.Overdue ? BadgeKind.Danger : Item.Status == "partial" ? BadgeKind.Info : Item.Status == "due" ? BadgeKind.Warning : BadgeKind.Neutral;
}

// ===================================================================== customer account
public sealed class CustomerDetailsViewModel : ObservableObject
{
    int _tab;
    public int Tab {get=>_tab;set=>Set(ref _tab,value);}
    readonly ShellViewModel _shell;
    readonly Action? _changed;

    public CustomerDetailsViewModel(ShellViewModel shell, CustomerItem customer, Action? changed)
    {
        _shell = shell;
        _changed = changed;
        Customer = customer;
        Loyalty=new CustomerLoyaltyViewModel(shell,customer.Id);
        var today = shell.S.Queries.Today;
        var invoices = shell.S.Queries.Invoices("2000-01-01", today, customerId: customer.Id, limit: 300);
        Invoices = invoices.Select(i => new InvoiceItem(i)).ToList();
        Installments = invoices.SelectMany(i => shell.S.Installments.Schedule(i.Id)).Select(r => new InstallmentItem(r))
            .OrderBy(i => i.DueDate, StringComparer.Ordinal).ToList();
        Collections = shell.S.Collections.History(customer.Id).Select(r => new CollectionItem(r)).ToList();
        Statement = shell.S.Customers.Statement(customer.Id).Reverse().Take(80).Select(e => new CustomerStatementItem(e)).ToList();
        var due = shell.S.Credit.Summary(customer.Id);
        DueNow = due.DueTotal; Overdue = due.OverdueTotal;
        Stats = shell.S.Customers.Stats(customer.Id);

        CollectCommand = new RelayCommand(() => _shell.ShowDialog(new CollectPaymentViewModel(_shell, Customer, () => _changed?.Invoke())), () => Customer.Outstanding > 0 && _shell.HasShift);
        EditCommand = new RelayCommand(() => _shell.ShowDialog(new CustomerEditViewModel(_shell, Customer, () => _changed?.Invoke())), () => _shell.IsOwner);
        OpeningCommand = new RelayCommand(() => _shell.ShowDialog(new CustomerOpeningViewModel(_shell, Customer, () => _changed?.Invoke())), () => _shell.IsOwner);
        ViewInvoiceCommand = new RelayCommand(p => _shell.ShowDialog(new InvoiceViewModel(_shell, (InvoiceItem)p!)));
        RescheduleCommand = new RelayCommand(p => _shell.ShowDialog(new RescheduleViewModel(_shell, (InstallmentItem)p!, () => _changed?.Invoke())), p => _shell.IsOwner);
        HistoryCommand = new RelayCommand(p => _shell.ShowDialog(new PlanHistoryViewModel(_shell, (InstallmentItem)p!)));
        ReverseCommand = new RelayCommand(p => Reverse((CollectionItem)p!), p => _shell.IsOwner && _shell.HasShift);
    }

    public Localizer L => Localizer.Instance;
    public CustomerItem Customer { get; }
    public CustomerLoyaltyViewModel Loyalty {get;}
    public IReadOnlyList<InvoiceItem> Invoices { get; }
    public IReadOnlyList<InstallmentItem> Installments { get; }
    public IReadOnlyList<CollectionItem> Collections { get; }
    public IReadOnlyList<CustomerStatementItem> Statement { get; }
    public decimal DueNow { get; }
    public decimal Overdue { get; }
    public CustomerStats Stats { get; }
    public bool HasNoPurchases => Stats.Invoices == 0;
    public string ReturnsText => $"{Stats.Returns} · {Stats.ReturnsTotal:N2}";
    static string Day(string? iso) => iso is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "—";
    public string FirstPurchase => Day(Stats.FirstPurchase);
    public string LastPurchase => Day(Stats.LastPurchase);
    public ICommand CollectCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand OpeningCommand { get; }
    public ICommand ViewInvoiceCommand { get; }
    public ICommand RescheduleCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand ReverseCommand { get; }
    public int InvoiceCount => Invoices.Count;
    public bool IsOwner => _shell.IsOwner;
    public bool CanCollect => Customer.Outstanding > 0;
    public bool HasInstallments => Installments.Count > 0;

    void Reverse(CollectionItem c) =>
        _shell.ShowDialog(new ReasonViewModel(_shell, L["Col_ReverseTitle"], $"{c.Number} · {L.Money(c.Amount)}", reason =>
        {
            try
            {
                _shell.S.Collections.Reverse(_shell.User!.Id, c.Id, reason, _shell.Shift!.Id);
                _shell.Refresh();
                _changed?.Invoke();
                _shell.Toast(L["Toast_Saved"]);
            }
            catch (PosException ex) { _shell.ToastError(ex); }
        }));
}

// ===================================================================== dialogs
public sealed class DebtAllocEdit : ObservableObject
{
    readonly Action _changed;
    string _amount = "0";

    public DebtAllocEdit(DebtSlice s, Action changed) { Slice = s; _changed = changed; }

    public DebtSlice Slice { get; }
    public string Document => Slice.Kind == "opening" ? Localizer.Instance["Sup_OpeningEntry"] : Slice.Document;
    public string Due => Slice.DueDate ?? "";
    public decimal Remaining => Slice.Remaining;
    public bool Overdue => Slice.Overdue;
    public string AmountText { get => _amount; set { if (Set(ref _amount, value)) _changed(); } }
    public decimal Amount => decimal.TryParse(_amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : -1m;
    public void SetAmount(decimal v) { _amount = v.ToString("0.##", CultureInfo.InvariantCulture); Raise(nameof(AmountText)); }
}

/// <summary>Collect a payment toward existing debt: earliest due first by default; the owner can adjust the split before posting.</summary>
public sealed class CollectPaymentViewModel : DialogViewModel
{
    readonly Action _done;
    readonly Guid _operationId = Guid.NewGuid();
    string _amount, _date, _ref = "", _notes = "";
    PaymentMethodOption _method;
    bool _updating, _posting;

    public CollectPaymentViewModel(ShellViewModel shell, CustomerItem customer, Action done) : base(shell)
    {
        Customer = customer;
        _done = done;
        _date = shell.S.Queries.Today;
        Methods = PaymentChoiceLists.Incoming(shell.S);
        _method = PaymentChoiceLists.First(Methods);
        foreach (var s in shell.S.Collections.OpenDebts(customer.Id)) Rows.Add(new DebtAllocEdit(s, OnRowChanged));
        _amount = customer.Outstanding.ToString("0.##", CultureInfo.InvariantCulture);
        Distribute();
        CollectCommand = new RelayCommand(Collect, () => !_posting);
    }

    public override double Width => 620;
    public CustomerItem Customer { get; }
    public ObservableCollection<DebtAllocEdit> Rows { get; } = [];
    public IReadOnlyList<PaymentMethodOption> Methods { get; }
    public ManualCardEntry Card {get;}=new();
    public bool IsCard=>Method.Kind=="card";
    public ICommand CollectCommand { get; }
    public bool CanAdjust => Shell.IsOwner;
    public bool IsReadOnlyAlloc => !Shell.IsOwner;
    public PaymentMethodOption Method { get => _method; set {if(Set(ref _method,value)){Raise(nameof(IsCard));Card.Confirmed=false;}} }
    public string Amount { get => _amount; set { if (Set(ref _amount, value)) { Card.Confirmed = false; SetError(nameof(Amount), null); Distribute(); } } }
    public string PayDate { get => _date; set { if (Set(ref _date, value)) SetError(nameof(PayDate), null); } }
    public string Reference { get => _ref; set => Set(ref _ref, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    decimal AmountValue => decimal.TryParse(_amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    public decimal AllocatedTotal => Rows.Sum(r => Math.Max(0, r.Amount));

    void Distribute()
    {
        _updating = true;
        foreach (var r in Rows) r.SetAmount(0);
        try
        {
            if (AmountValue > 0 && AmountValue <= Customer.Outstanding)
                foreach (var a in Shell.S.Collections.Propose(Customer.Id, AmountValue))
                {
                    var row = Rows.FirstOrDefault(r => r.Slice.InstallmentId == a.InstallmentId && r.Slice.Id == (a.SaleId ?? a.OpeningId) && r.Slice.Kind == (a.OpeningId is not null ? "opening" : "sale"));
                    row?.SetAmount(a.Amount);
                }
        }
        catch (PosException) { /* amount over the debt: the field shows the error on submit */ }
        _updating = false;
        Raise(nameof(AllocatedTotal));
    }

    void OnRowChanged() { if (!_updating) Raise(nameof(AllocatedTotal)); }

    void Collect()
    {
        var l = Localizer.Instance;
        SetError(nameof(PayDate), DateOnly.TryParseExact(_date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : l["Credit_BadDate"]);
        var v = AmountValue;
        SetError(nameof(Amount), v <= 0 ? l["Err_Number"] : v > Customer.Outstanding ? l["Err_AboveOutstanding"] : null);
        if (v > 0 && Money.ToCents(AllocatedTotal) != Money.ToCents(v)) SetError(nameof(Amount), string.Format(l["Sup_AllocSum"], l.Money(v), l.Money(AllocatedTotal)));
        if (HasErrors) return;
        if (!Shell.HasShift) { SetError(nameof(Amount), l["Toast_NeedShift"]); return; }
        _posting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var method = _method.MethodKind;
            var alloc = Shell.IsOwner
                ? Rows.Where(r => r.Amount > 0).Select(r => new DebtAllocation(r.Slice.Kind == "sale" ? r.Slice.Id : null, r.Slice.Kind == "opening" ? r.Slice.Id : null, r.Slice.InstallmentId, r.Amount)).ToList()
                : null;
            var result = Shell.S.Collections.Collect(new CollectRequest(_operationId, Customer.Id, v, method, Shell.Shift!.Id, Shell.User!.Id, _date,
                string.IsNullOrWhiteSpace(_ref) ? null : _ref.Trim(), string.IsNullOrWhiteSpace(_notes) ? null : _notes.Trim(), alloc,_method.Id,IsCard?Card.Confirmation:null));
            Shell.CloseDialog();
            _done();
            Shell.Refresh();
            Shell.Toast($"{l["Toast_Collected"]} ({result.Number})");
        }
        catch (PosException ex) { SetError(nameof(Amount), Shell.ErrorText(ex)); }
        finally { _posting = false; CommandManager.InvalidateRequerySuggested(); }
    }
}

/// <summary>Owner-only: move the dates of what is still unpaid on an installment sale. The debt itself never changes.</summary>
public sealed class RescheduleViewModel : DialogViewModel
{
    readonly long _saleId;
    readonly Action _done;
    string _reason = "";

    public RescheduleViewModel(ShellViewModel shell, InstallmentItem item, Action done) : base(shell)
    {
        _saleId = item.SaleId; _done = done;
        Invoice = item.Invoice;
        Remaining = shell.S.Collections.Outstanding(_saleId);
        Plan = new CreditPlanState();
        Plan.Configure(shell, Remaining);
        SaveCommand = new RelayCommand(Save);
    }

    public override double Width => 520;
    public string Invoice { get; }
    public decimal Remaining { get; }
    public CreditPlanState Plan { get; }
    public ICommand SaveCommand { get; }
    public string Reason { get => _reason; set { if (Set(ref _reason, value)) SetError(nameof(Reason), null); } }

    void Save()
    {
        var l = Localizer.Instance;
        SetError(nameof(Reason), string.IsNullOrWhiteSpace(_reason) ? l["Err_Required"] : null);
        if (!Plan.IsValid || Plan.Rows.Count == 0) { SetError(nameof(Reason), Plan.Error.Length > 0 ? Plan.Error : l["Credit_PlanNeeded"]); return; }
        if (HasErrors) return;
        try
        {
            Shell.S.Installments.Reschedule(Shell.User!.Id, _saleId, _reason.Trim(), Plan.Rows.Select(r => new InstallmentInput(r.DueDate, r.Amount)).ToList());
            Shell.CloseDialog();
            _done();
            Shell.Toast(l["Toast_Saved"]);
        }
        catch (PosException ex) { SetError(nameof(Reason), Shell.ErrorText(ex)); }
    }
}

public sealed class PlanHistoryViewModel : DialogViewModel
{
    public PlanHistoryViewModel(ShellViewModel shell, InstallmentItem item) : base(shell)
    {
        Invoice = item.Invoice;
        Versions = shell.S.Installments.History(item.SaleId).Select(v => new PlanVersionView(v)).Reverse().ToList();
    }

    public override double Width => 560;
    public string Invoice { get; }
    public IReadOnlyList<PlanVersionView> Versions { get; }
}

public sealed class PlanVersionView : LocalizedObject
{
    public PlanVersionView(PlanVersion v) { Version = v; Rows = v.Installments.Select(i => new InstallmentItem(i)).ToList(); }
    public PlanVersion Version { get; }
    public string Title => $"{Localizer.Instance["Plan_Version"]} {Version.Version} · {Localizer.Instance["Plan_" + Version.Kind]}" + (Version.Active ? $" · {Localizer.Instance["Plan_Current"]}" : "");
    public string Detail => $"{Version.CreatedAt[..Math.Min(10, Version.CreatedAt.Length)]}" + (string.IsNullOrWhiteSpace(Version.Reason) ? "" : $" · {Version.Reason}");
    public IReadOnlyList<InstallmentItem> Rows { get; }
}

public sealed class CustomerOpeningViewModel : DialogViewModel
{
    readonly CustomerItem _customer;
    readonly Action _done;
    readonly Guid _op = Guid.NewGuid();
    string _amount = "", _note = "";

    public CustomerOpeningViewModel(ShellViewModel shell, CustomerItem customer, Action done) : base(shell) { _customer = customer; _done = done; }

    public override double Width => 460;
    public string CustomerName => _customer.Name;
    public string Amount { get => _amount; set { if (Set(ref _amount, value)) SetError(nameof(Amount), null); } }
    public string Note { get => _note; set => Set(ref _note, value); }
    public ICommand SaveCommand => new RelayCommand(Save);

    void Save()
    {
        var l = Localizer.Instance;
        var ok = decimal.TryParse(_amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0;
        SetError(nameof(Amount), ok ? null : l["Err_Number"]);
        if (HasErrors) return;
        try
        {
            Shell.S.Credit.AddOpeningBalance(_op, Shell.User!.Id, _customer.Id, v, string.IsNullOrWhiteSpace(_note) ? null : _note.Trim());
            Shell.CloseDialog();
            _done();
            Shell.Refresh();
            Shell.Toast(l["Toast_Saved"]);
        }
        catch (PosException ex) { SetError(nameof(Amount), Shell.ErrorText(ex)); }
    }
}

/// <summary>Due and overdue amounts across all customers.</summary>
public sealed class DueReportViewModel : DialogViewModel
{
    bool _overdueOnly;
    List<DueItemView> _all = [];

    public DueReportViewModel(ShellViewModel shell) : base(shell)
    {
        Reload();
        ExportCommand = new RelayCommand(Export);
    }

    public override double Width => 820;
    public ICommand ExportCommand { get; }
    public IReadOnlyList<DueItemView> Items => _overdueOnly ? _all.Where(i => i.Overdue).ToList() : _all;
    public bool OverdueOnly { get => _overdueOnly; set { if (Set(ref _overdueOnly, value)) Raise(nameof(Items)); } }
    public decimal Outstanding { get; private set; }
    public decimal DueTotal { get; private set; }
    public decimal OverdueTotal { get; private set; }

    void Reload()
    {
        _all = Shell.S.Credit.DueItems().Select(d => new DueItemView(d)).ToList();
        var s = Shell.S.Credit.Summary();
        (Outstanding, DueTotal, OverdueTotal) = (s.Outstanding, s.DueTotal, s.OverdueTotal);
    }

    void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV|*.csv", FileName = $"dukkan-customer-dues-{Shell.S.Queries.Today}.csv" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            CsvExport.Write(dlg.FileName, ["Customer", "Document", "Due date", "Remaining", "Overdue"],
                Items.Select(i => new[] { i.Customer, i.Document, i.Due, i.Remaining.ToString("0.00", CultureInfo.InvariantCulture), i.Overdue ? "yes" : "no" }));
            Shell.Toast(Localizer.Instance["Toast_Exported"]);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Shell.Toast(Localizer.Instance["Toast_CopyFailed"], BadgeKind.Danger);
        }
    }
}
