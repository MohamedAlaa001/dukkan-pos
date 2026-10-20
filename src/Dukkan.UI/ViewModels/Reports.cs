using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace Dukkan.UI;

public sealed class CashierOption(long? id, string name) : LocalizedObject
{
    public long? Id { get; } = id;
    public string Name => Id is null ? Localizer.Instance["Cashier_All"] : name;
    public override string ToString() => Name;
}

public sealed class ReportsViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    int _tab;
    bool _loading;
    string _from, _to;
    string _search = "";
    CashierOption _cashier;
    Option _statusFilter, _payFilter;
    DashboardStats _stats = new(0, 0, 0, 0);
    IReadOnlyList<DailyPoint> _daily = [];
    string _dateError = "";

    public ReportsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Smart=new SmartReportsViewModel(shell);
        var today = DateTime.ParseExact(shell.S.Queries.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        _to = today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        _from = today.AddDays(-13).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        Cashiers = [new CashierOption(null, ""), .. shell.S.Users.List().Select(u => new CashierOption(u.Id, u.DisplayName))];
        _cashier = Cashiers[0];
        StatusFilters = new[] {"Inv_All", "Inv_Paid", "Inv_Partial", "Inv_Returned"}.Select(k => new Option(k)).ToList();
        PayFilters = new[] {"Pay_All", "Pay_Cash", "Pay_Card", "Pay_Transfer", "Pay_Credit", "Pay_Split"}.Select(k => new Option(k)).ToList();
        _statusFilter = StatusFilters[0];
        _payFilter = PayFilters[0];
        TopProducts = new ObservableCollection<TopProductRow>();
        Pager = new Pager<InvoiceItem>(8);
        ViewInvoiceCommand = new RelayCommand(p => _shell.ShowDialog(new InvoiceViewModel(_shell, (InvoiceItem)p!)));
        ExportCommand = new RelayCommand(Export);
        Shifts = [];
        Localizer.Instance.PropertyChanged += (_, _) => Raise(nameof(Series));
        Reload();
    }

    public Localizer L => Localizer.Instance;
    public SmartReportsViewModel Smart{get;}
    public IReadOnlyList<CashierOption> Cashiers { get; }
    public IReadOnlyList<Option> StatusFilters { get; }
    public IReadOnlyList<Option> PayFilters { get; }
    public ObservableCollection<TopProductRow> TopProducts { get; }
    public ObservableCollection<ShiftHistoryItem> Shifts { get; }
    public Pager<InvoiceItem> Pager { get; }
    public ICommand ViewInvoiceCommand { get; }
    public ICommand ExportCommand { get; }

    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public string From { get => _from; set { if (Set(ref _from, value)) ShellViewModel.SimulateLoading(b => IsLoading = b, Reload, 200); } }
    public string To { get => _to; set { if (Set(ref _to, value)) ShellViewModel.SimulateLoading(b => IsLoading = b, Reload, 200); } }
    public CashierOption Cashier { get => _cashier; set { if (Set(ref _cashier, value)) ShellViewModel.SimulateLoading(b => IsLoading = b, Reload, 200); } }
    public string Search { get => _search; set { if (Set(ref _search, value)) ApplyHistory(); } }
    public Option StatusFilter { get => _statusFilter; set { if (Set(ref _statusFilter, value)) ApplyHistory(); } }
    public Option PayFilter { get => _payFilter; set { if (Set(ref _payFilter, value)) ApplyHistory(); } }
    public string DateError { get => _dateError; private set => Set(ref _dateError, value); }

    public decimal TotalSales => _stats.Sales;
    public int TotalInvoices => _stats.Invoices;
    public int ItemsSold => _stats.Units;
    public decimal AvgInvoice => _stats.Invoices == 0 ? 0 : Math.Round(_stats.Sales / _stats.Invoices);
    public decimal GrossProfit => _stats.GrossProfit;
    public IReadOnlyList<PaymentTotalsItem> PaymentTotals {get;private set;}=[];
    public ICommand ExportMethodsCommand=>new RelayCommand(()=>
    {
        var l=Localizer.Instance;var save=new Microsoft.Win32.SaveFileDialog{Filter="CSV|*.csv",FileName="dukkan-payment-methods.csv"};
        if(save.ShowDialog()!=true)return;
        try{CsvExport.Write(save.FileName,[l["Pay_Method"],l["PO_Code"],l["Pay_SalesReceived"],l["Pay_Collections"],l["Pay_Refunds"],l["Pay_SupplierPaid"],l["Pay_NetReceived"]],
            PaymentTotals.Select(p=>new[]{p.Name,p.Row.Code,p.Row.Sales.ToString("0.00",CultureInfo.InvariantCulture),p.Row.Collections.ToString("0.00",CultureInfo.InvariantCulture),p.Row.Refunds.ToString("0.00",CultureInfo.InvariantCulture),p.Row.SupplierPayments.ToString("0.00",CultureInfo.InvariantCulture),p.Net.ToString("0.00",CultureInfo.InvariantCulture)}));_shell.Toast(l["Toast_Exported"]);}
        catch(Exception ex)when(ex is System.IO.IOException or UnauthorizedAccessException){_shell.Toast(l["Toast_CopyFailed"],BadgeKind.Danger);}
    });

    public IReadOnlyList<ChartPoint> Series => _daily.Select(d =>
        new ChartPoint(DateTime.Parse(d.Date, CultureInfo.InvariantCulture).ToString("MMM dd", ShellViewModel.Culture), (double)d.Sales)).ToList();

    bool TryDates(out string from, out string to)
    {
        from = to = "";
        var ok = DateTime.TryParseExact(_from, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
                 & DateTime.TryParseExact(_to, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t);
        if (!ok || f > t || (t - f).TotalDays > 366) { DateError = Localizer.Instance["Err_DateRange"]; return false; }
        DateError = "";
        from = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        to = t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    public void Reload()
    {
        if (!TryDates(out var from, out var to)) return;
        var q = _shell.S.Queries;
        _stats = q.Stats(from, to, _cashier.Id);
        _daily = q.Daily(from, to, _cashier.Id);
        Smart.Reload(from,to,_cashier.Id);
        PaymentTotals=_shell.S.Reports.PaymentBreakdown(from,to,_cashier.Id).Select(p=>new PaymentTotalsItem(p)).ToList();Raise(nameof(PaymentTotals));
        TopProducts.Clear();
        var n = 1;
        foreach (var t in q.TopSellers(from, to, 5, _cashier.Id)) TopProducts.Add(new TopProductRow { No = n++, NameEn = t.NameEn, NameAlt = t.NameAlt, Qty = t.Units, Total = t.Sales });
        Shifts.Clear();
        foreach (var sh in q.ShiftHistory(100000,_cashier.Id,from,to))
            Shifts.Add(new ShiftHistoryItem(sh));
        ApplyHistory();
        Raise(nameof(TotalSales)); Raise(nameof(TotalInvoices)); Raise(nameof(ItemsSold)); Raise(nameof(AvgInvoice)); Raise(nameof(GrossProfit)); Raise(nameof(Series));
    }

    void ApplyHistory()
    {
        if (!TryDates(out var from, out var to)) return;
        var status = _statusFilter.Key == "Inv_All" ? null : _statusFilter.Key["Inv_".Length..];
        var pay = _payFilter.Key == "Pay_All" ? null : _payFilter.Key["Pay_".Length..];
        Pager.SetSource(_shell.S.Queries.Invoices(from, to, string.IsNullOrWhiteSpace(_search) ? null : _search.Trim(), status, pay, 3000, null, _cashier.Id)
            .Select(i => new InvoiceItem(i)).ToList());
    }

    /// <summary>CSV export of the invoices in range (Excel-safe: formula characters are neutralised, UTF-8 with BOM for Arabic).</summary>
    void Export()
    {
        if(Tab==4){Smart.ExportCommand.Execute(null);return;}
        if(Tab==3){ExportMethodsCommand.Execute(null);return;}
        var l=Localizer.Instance;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV|*.csv", FileName = $"dukkan-sales-{_shell.S.Queries.Today}.csv" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (!TryDates(out var from, out var to)) return;
            var status = _statusFilter.Key == "Inv_All" ? null : _statusFilter.Key["Inv_".Length..];
            var pay = _payFilter.Key == "Pay_All" ? null : _payFilter.Key["Pay_".Length..];
            var rows = _shell.S.Queries.Invoices(from, to, string.IsNullOrWhiteSpace(_search) ? null : _search.Trim(), status, pay, 100000, null, _cashier.Id);
            if(Tab==0){CsvExport.Write(dlg.FileName,[l["Col_Date"],l["Smart_NetSales"]],_daily.Select(r=>new[]{r.Date,r.Sales.ToString("0.00",CultureInfo.InvariantCulture)}));_shell.Toast(l["Toast_Exported"]);return;}
            if(Tab==2){CsvExport.Write(dlg.FileName,[l["Col_Date"],l["Col_Cashier"],l["Shift_Float"],l["Shift_Expected"],l["Shift_Counted"],l["Shift_Variance"]],Shifts.Select(r=>new[]{r.Date,r.Cashier,r.Float.ToString("0.00",CultureInfo.InvariantCulture),r.Expected,r.Counted,r.Variance}));_shell.Toast(l["Toast_Exported"]);return;}
            CsvExport.Write(dlg.FileName, [l["Col_Invoice"], l["Col_Date"], l["Col_Customer"], l["Col_Cashier"], l["Col_Items"], l["Col_Payment"], l["Col_Total"], l["Col_Status"]],
                rows.Select(r => new[] { r.Number, r.CreatedAt, r.Customer ?? "", r.Cashier, r.Units.ToString(CultureInfo.InvariantCulture), r.PaymentNameEn is not null?l.Pick(r.PaymentNameEn,r.PaymentNameAr??r.PaymentNameEn):l["Pay_"+r.PaymentKey],
                    r.Total.ToString("0.00", CultureInfo.InvariantCulture), l["Inv_"+r.StatusKey] }));
            _shell.Toast(Localizer.Instance["Toast_Exported"]);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _shell.Toast(ex.Message, BadgeKind.Danger);
        }
    }
}

public static class CsvExport
{
    /// <summary>Writes RFC-4180 CSV; cells starting with = + - @ are prefixed so spreadsheets never run them as formulas.</summary>
    public static void Write(string path, IReadOnlyList<string> header, IEnumerable<string[]> rows)
    {
        static string Cell(string v)
        {
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0]) && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) v = "'" + v;
            return v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        using var w = new System.IO.StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        w.WriteLine(string.Join(",", header.Select(Cell)));
        foreach (var r in rows) w.WriteLine(string.Join(",", r.Select(Cell)));
    }
}

// ===================================================================== invoice details + returns
public sealed class InvoiceViewModel : DialogViewModel
{
    readonly InvoiceItem _invoice;
    readonly ReceiptModel _receipt;
    string _status = "";
    BadgeKind _statusKind = BadgeKind.Info;

    public InvoiceViewModel(ShellViewModel shell, InvoiceItem invoice) : base(shell)
    {
        _invoice = invoice;
        _receipt = shell.S.Sales.GetReceipt(invoice.Id);
        Lines = _receipt.Lines.Select(l => new InvoiceLine(l)).ToList();
        PrintCommand = new RelayCommand(() => Reprint("receipt80"));
        PrintA4Command = new RelayCommand(() => Reprint("a4"));
        SavePdfCommand = new RelayCommand(SavePdf);
        ReturnCommand = new RelayCommand(() => Shell.ShowDialog(new ReturnViewModel(Shell, invoice)), () => invoice.StatusKey != "Returned" && Shell.HasShift);
    }

    public override double Width => 540;
    public InvoiceItem Invoice => _invoice;
    public IReadOnlyList<InvoiceLine> Lines { get; }
    public decimal Total => _receipt.Total;
    public decimal Discount => _receipt.Discount;
    public bool HasDiscount => _receipt.Discount > 0;
    public decimal Change => _receipt.Change;
    public decimal Debt => _receipt.Debt;
    public bool HasDebt => _receipt.Debt > 0;
    public ICommand PrintCommand { get; }
    public ICommand PrintA4Command { get; }
    public ICommand SavePdfCommand { get; }
    public ICommand ReturnCommand { get; }
    public string Status { get => _status; private set { Set(ref _status, value); Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    public BadgeKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    /// <summary>Always a marked copy: the original receipt already exists, so reprints are labelled and never alter the sale.</summary>
    void Reprint(string kind)
    {
        var l = Localizer.Instance;
        var status = Shell.S.Printing.Reprint(_invoice.Id, kind);
        var last = Shell.S.PrintJobs.ForSale(_invoice.Id).LastOrDefault();
        Status = status == PrintStatus.Submitted ? l["Print_Sent"] : string.Format(l["Print_Failed"], last?.Error ?? "");
        StatusKind = status == PrintStatus.Submitted ? BadgeKind.Success : BadgeKind.Danger;
        Shell.RefreshNotifications();
    }

    void SavePdf()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PDF|*.pdf", FileName = $"{_invoice.Number}.pdf" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Shell.S.Printing.SavePdf(_invoice.Id, dlg.FileName, "a4", isCopy: true);
            Status = Localizer.Instance["Print_PdfSaved"];
            StatusKind = BadgeKind.Success;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
            StatusKind = BadgeKind.Danger;
        }
    }
}

public sealed class InvoiceLine(ReceiptLine l)
{
    public ReceiptLine Line { get; } = l;
    public string Name => Line.Name;
    public string Detail => $"{string.Join(" / ", new[] { Line.Size, Line.Color }.Where(x => !string.IsNullOrWhiteSpace(x)))}  ·  {Line.Quantity:0.###} × {Line.UnitPrice:N0}";
    public decimal Total => Line.Total;
}

public sealed class ReturnRow(SaleService.ReturnableLine l) : ObservableObject
{
    string _qty = "0";
    bool _resalable = true;

    public SaleService.ReturnableLine Line { get; } = l;
    public decimal Available => Line.Sold - Line.AlreadyReturned;
    public string Name => Line.Name;
    public string Variant => string.Join(" / ", new[] { Line.Size, Line.Color }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public Action? Changed { get; set; }
    public string Qty { get => _qty; set { if (Set(ref _qty, value)) Changed?.Invoke(); } }
    public bool Resalable { get => _resalable; set => Set(ref _resalable, value); }
    public IReadOnlyList<StockLotRow> Lots { get; set; } = [];
    public StockLotRow? Lot { get; set; }
    public decimal QtyValue => decimal.TryParse(_qty, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : -1;
}

/// <summary>Return of posted items. Needs an owner: the owner themself, or a named approving owner for a cashier.</summary>
public sealed class ReturnViewModel : DialogViewModel
{
    readonly InvoiceItem _invoice;
    readonly SaleService.ReturnableSale _sale;
    readonly Guid _operationId = Guid.NewGuid();
    string _reason = "";
    PaymentMethodOption _selectedMethod=null!;

    public ReturnViewModel(ShellViewModel shell, InvoiceItem invoice) : base(shell)
    {
        _invoice = invoice;
        _sale = shell.S.Sales.FindForReturn(invoice.Number) ?? throw new PosException(PosError.NotFound, "Sale not found.");
        Rows = _sale.Lines.Select(l => new ReturnRow(l)).Where(r => r.Available > 0).ToList();
        foreach (var r in Rows) {r.Changed = UpdatePreview;r.Lots=shell.S.Stock.Lots(r.Line.ProductId);}
        Methods = new[]{new PaymentMethodOption(new(0,"original",Strings.Get("Pay_OriginalMethods",false),Strings.Get("Pay_OriginalMethods",true),"original",true,0),true)}.Concat(PaymentChoiceLists.Incoming(shell.S)).ToList();
        SelectedMethod = Methods[0];
        PostCommand = new RelayCommand(Post);
    }

    public override double Width => 560;
    public InvoiceItem Invoice => _invoice;
    public IReadOnlyList<ReturnRow> Rows { get; }
    public IReadOnlyList<PaymentMethodOption> Methods { get; }
    public PaymentMethodOption SelectedMethod { get=>_selectedMethod;set {if(Set(ref _selectedMethod,value))UpdatePreview();} }
    public IReadOnlyList<RefundPaymentItem> RefundPayments {get;private set;}=[];
    public string Reason { get => _reason; set { if (Set(ref _reason, value)) SetError(nameof(Reason), null); } }
    public ICommand PostCommand { get; }
    public decimal PreviewTotal { get; private set; }
    public decimal PreviewDebt { get; private set; }
    public decimal PreviewRefund { get; private set; }
    public bool HasPreview { get; private set; }
    public IReadOnlyList<InstallmentRow> RevisedSchedule { get; private set; } = [];
    public bool HasSchedule => RevisedSchedule.Count > 0;
    public bool HasRefund => PreviewRefund > 0;

    /// <summary>Shows how the return clears debt first, what is refunded, and the revised installment schedule - before anything is posted.</summary>
    void UpdatePreview()
    {
        var lines = Rows.Where(r => r.QtyValue > 0 && r.QtyValue <= r.Available)
            .Select(r => new ReturnLineRequest(r.Line.SaleLineId, r.QtyValue, r.Resalable,r.Lot?.Id)).ToList();
        try
        {
            if (lines.Count == 0) { HasPreview = false; RevisedSchedule = []; RefundPayments=[]; }
            else
            {
                var p = Shell.S.Returns.Preview(_sale.SaleId, lines,SelectedMethod.Id==0?null:SelectedMethod.MethodKind,SelectedMethod.Id==0?null:SelectedMethod.Id);
                (PreviewTotal, PreviewDebt, PreviewRefund, HasPreview) = (p.Total, p.DebtReduction, p.Refund, true);
                RevisedSchedule = p.DebtReduction > 0 ? Shell.S.Installments.PreviewReduction(_sale.SaleId, p.DebtReduction).Where(r => r.Remaining > 0).ToList() : [];
                RefundPayments=(p.Payments??[]).Select(r=>new RefundPaymentItem(r)).ToList();
            }
        }
        catch (PosException) { HasPreview = false; RevisedSchedule = []; RefundPayments=[]; }
        foreach (var n in new[] { nameof(PreviewTotal), nameof(PreviewDebt), nameof(PreviewRefund), nameof(HasPreview), nameof(RevisedSchedule), nameof(HasSchedule), nameof(HasRefund) }) Raise(n);
        Raise(nameof(RefundPayments));
    }

    void Post()
    {
        var l = Localizer.Instance;
        var picked = Rows.Where(r => r.QtyValue != 0).ToList();
        SetError("Rows", picked.Count == 0 ? l["Err_NothingToReturn"] : picked.Any(r => r.QtyValue < 0 || r.QtyValue > r.Available) ? l["Err_ReturnQty"] : null);
        SetError(nameof(Reason), string.IsNullOrWhiteSpace(_reason) ? l["Err_Required"] : null);
        if (HasErrors) return;
        if (Shell.IsOwner) Submit(null);
        else Shell.ShowDialog(new ApprovalViewModel(Shell, $"{l["Ret_Title"]}: {_invoice.Number}", owner => { Shell.ShowDialog(this); Submit(owner); }));
    }

    void Submit(long? approver)
    {
        var l = Localizer.Instance;
        try
        {
            PaymentMethod? method = SelectedMethod.Id==0?null:SelectedMethod.MethodKind;
            var req = new PostReturnRequest(_operationId, _sale.SaleId, Shell.Shift!.Id, Shell.User!.Id, approver, _reason.Trim(),
                Rows.Where(r => r.QtyValue > 0).Select(r => new ReturnLineRequest(r.Line.SaleLineId, r.QtyValue, r.Resalable,r.Lot?.Id)).ToList(), method,SelectedMethod.Id==0?null:SelectedMethod.Id);
            var result = Shell.S.Returns.Post(req);
            Shell.CloseDialog();
            Shell.Refresh();
            Shell.Toast(string.Format(l["Toast_ReturnDone"], result.Number, l.Money(result.Refund)));
        }
        catch (PosException ex) { SetError("Rows", Shell.ErrorText(ex)); }
    }
}
public sealed class RefundPaymentItem(RefundPaymentRow row) : LocalizedObject
{
    public string Method=>Localizer.Instance.Pick(row.NameEn,row.NameAr);
    public decimal Amount=>row.Amount;
}
public sealed class PaymentTotalsItem(PaymentMethodTotals row) : LocalizedObject
{
    public PaymentMethodTotals Row {get;}=row;
    public string Name=>Localizer.Instance.Pick(Row.NameEn,Row.NameAr);
    public decimal Net=>Row.Net;
}
