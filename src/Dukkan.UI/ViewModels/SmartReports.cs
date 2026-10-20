using System.Globalization;
using System.Windows.Input;
using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace Dukkan.UI;

public sealed class SmartReportOption(string key,ProfitGroup? group=null):LocalizedObject
{
    public string Key{get;}=key;public ProfitGroup? Group{get;}=group;public string Name=>Localizer.Instance[Key];public override string ToString()=>Name;
}
public sealed class SmartProfitItem(ProfitRow row,bool category=false):LocalizedObject
{
    public ProfitRow Row{get;}=row;public string Name=>category?CategoryNames.Label(Row.NameEn):string.IsNullOrWhiteSpace(Row.NameAr)?Row.NameEn:Localizer.Instance.Pick(Row.NameEn,Row.NameAr);public decimal Revenue=>Row.Revenue;
}
public sealed class SmartStockItem(StockValueRow row,bool dead=true):LocalizedObject
{
    public StockValueRow Row{get;}=row;public string Name=>string.IsNullOrWhiteSpace(Row.NameAr)?Row.NameEn:Localizer.Instance.Pick(Row.NameEn,Row.NameAr);public string LastSale=>dead?Row.LastSale??Localizer.Instance["Smart_Never"]:"";
}
public sealed class SmartReportsViewModel:ObservableObject
{
    readonly ShellViewModel _shell;string _from="",_to="",_days="30",_error="";long? _cashier;SmartReportOption _selected;
    public SmartReportsViewModel(ShellViewModel shell)
    {
        _shell=shell;Options=[new("Smart_Product",ProfitGroup.Product),new("Smart_Category",ProfitGroup.Category),new("Smart_Cashier",ProfitGroup.Cashier),new("Smart_Day",ProfitGroup.Day),new("Smart_Month",ProfitGroup.Month),new("Smart_Best"),new("Smart_Worst"),new("Smart_Dead"),new("Smart_Valuation"),new("Smart_Tax")];_selected=Options[0];
        ExportCommand=new RelayCommand(()=>{var save=new Microsoft.Win32.SaveFileDialog{Filter="CSV|*.csv",FileName=$"dukkan-report-{_to}.csv"};if(save.ShowDialog()==true)try{ExportTo(save.FileName);_shell.Toast(L["Toast_Exported"]);}catch(Exception ex)when(ex is System.IO.IOException or UnauthorizedAccessException){_shell.Toast(L["Toast_CopyFailed"],BadgeKind.Danger);}});
        DailyCommand=new RelayCommand(()=>{if(_to.Length>0)_shell.ShowDialog(new DailySummaryViewModel(_shell,_to,_cashier));});
    }
    public Localizer L=>Localizer.Instance;
    public IReadOnlyList<SmartReportOption> Options{get;}
    public SmartReportOption Selected{get=>_selected;set{if(Set(ref _selected,value)){Refresh();Raise(nameof(IsStock));Raise(nameof(IsTax));Raise(nameof(IsProfit));Raise(nameof(IsDead));Raise(nameof(Hint));}}}
    public bool IsStock=>Selected.Key is "Smart_Dead" or "Smart_Valuation";public bool IsTax=>Selected.Key=="Smart_Tax";public bool IsProfit=>!IsStock&&!IsTax;public bool IsDead=>Selected.Key=="Smart_Dead";
    public string Hint=>L[IsStock?"Smart_StockHint":"Smart_Hint"];
    public string Days{get=>_days;set{if(Set(ref _days,value))Refresh();}}
    public string Error{get=>_error;private set=>Set(ref _error,value);}
    public IReadOnlyList<SmartProfitItem> Rows{get;private set;}=[];
    public IReadOnlyList<SmartStockItem> Stock{get;private set;}=[];
    public IReadOnlyList<TaxReportRow> Taxes{get;private set;}=[];
    public bool IsEmpty=>Rows.Count==0&&Stock.Count==0&&Taxes.Count==0;
    public decimal Revenue=>Rows.Sum(r=>r.Row.Revenue);public decimal Cost=>Rows.Sum(r=>r.Row.Cost);public decimal Profit=>Revenue-Cost;
    public decimal CostValue=>Stock.Sum(r=>r.Row.CostValue);public decimal RetailValue=>Stock.Sum(r=>r.Row.RetailValue);
    public ICommand ExportCommand{get;}public ICommand DailyCommand{get;}
    public void Reload(string from,string to,long? cashier){_from=from;_to=to;_cashier=cashier;Refresh();}
    void Refresh()
    {
        if(_from.Length==0)return;Error="";Rows=[];Stock=[];Taxes=[];
        if(IsDead&&(!int.TryParse(Days,out var n)||n<1||n>36500))Error=L["Err_Number"];
        else if(IsStock)Stock=(IsDead?_shell.S.Reports.DeadStock(_to,int.Parse(Days,CultureInfo.InvariantCulture)):_shell.S.Reports.StockValuation()).Select(r=>new SmartStockItem(r,IsDead)).ToList();
        else if(IsTax)Taxes=_shell.S.Reports.TaxSummary(_from,_to,_cashier);
        else
        {
            IEnumerable<ProfitRow> rows=_shell.S.Reports.Profit(_from,_to,Selected.Group??ProfitGroup.Product,_cashier);
            if(Selected.Key=="Smart_Best")rows=rows.Where(r=>r.Quantity>0).OrderByDescending(r=>r.Quantity).ThenBy(r=>r.Key,StringComparer.Ordinal);
            if(Selected.Key=="Smart_Worst")rows=rows.Where(r=>r.Quantity>0).OrderBy(r=>r.Quantity).ThenBy(r=>r.Key,StringComparer.Ordinal);
            Rows=rows.Select(r=>new SmartProfitItem(r,Selected.Group==ProfitGroup.Category)).ToList();
        }
        foreach(var key in new[]{nameof(Rows),nameof(Stock),nameof(Taxes),nameof(Revenue),nameof(Cost),nameof(Profit),nameof(CostValue),nameof(RetailValue),nameof(IsEmpty)})Raise(key);
    }
    public void ExportTo(string path)
    {
        string N(decimal n)=>n.ToString("0.###",CultureInfo.InvariantCulture);
        if(IsStock)CsvExport.Write(path,[L["Col_Product"],L["Col_Qty"],L["Smart_CostValue"],L["Smart_RetailValue"],L["Smart_LastSale"]],Stock.Select(r=>new[]{r.Name,N(r.Row.Quantity),N(r.Row.CostValue),N(r.Row.RetailValue),r.LastSale}));
        else if(IsTax)CsvExport.Write(path,[L["Smart_Rate"],L["Smart_Revenue"],L["Smart_TaxAmount"]],Taxes.Select(r=>new[]{N(r.Rate),N(r.Taxable),N(r.Tax)}));
        else CsvExport.Write(path,[L["Smart_Name"],L["Col_Qty"],L["Smart_Revenue"],L["Smart_Cost"],L["Smart_Profit"],L["Smart_Margin"],L["Smart_Discounts"],L["Smart_Returns"],L["Smart_TaxAmount"]],Rows.Select(r=>new[]{r.Name,N(r.Row.Quantity),N(r.Row.Revenue),N(r.Row.Cost),N(r.Row.Profit),N(r.Row.Margin),N(r.Row.Discounts),N(r.Row.Returns),N(r.Row.Tax)}));
    }
    public static IReadOnlyDictionary<string,string> DocumentLabels(bool arabic)=>new[]{"Daily_Title","Daily_Sales","Smart_Returns","Smart_Revenue","Smart_TaxAmount","Daily_Expenses","Daily_Opening","Daily_Expected","Daily_Counted","Daily_Uncounted","Daily_OpenShifts","Daily_Methods","Daily_Top","Daily_Hint"}.ToDictionary(k=>k,k=>Strings.Get(k,arabic));
}
public sealed record DailyMetric(string Name,string Value);
public sealed class DailySummaryViewModel:DialogViewModel
{
    public DailySummaryReport Report{get;}public string CashierName{get;}
    public override double Width=>700;
    public DailySummaryViewModel(ShellViewModel shell,string date,long? cashier):base(shell)
    {
        // A cashier may only open their own operational summary; it contains no cost/profit figures.
        if(!shell.IsOwner)cashier=shell.User!.Id;
        Report=shell.S.Reports.DailySummary(date,cashier);CashierName=cashier is null?L["Cashier_All"]:shell.S.Users.List().First(u=>u.Id==cashier).DisplayName;
        Print80Command=new RelayCommand(()=>Output("80"));PrintA4Command=new RelayCommand(()=>Output("a4"));PdfCommand=new RelayCommand(()=>Output("pdf"));CsvCommand=new RelayCommand(()=>Output("csv"));
    }
    public Localizer L=>Localizer.Instance;
    public IReadOnlyList<DailyMetric> Metrics=>new (string,decimal)[]{("Daily_Sales",Report.Sales),("Smart_Returns",Report.Returns),("Smart_Revenue",Report.Revenue),("Smart_TaxAmount",Report.Tax),("Daily_Expenses",Report.Expenses),("Daily_Opening",Report.OpeningCash),("Daily_Expected",Report.ExpectedCash)}.Select(p=>new DailyMetric(L[p.Item1],p.Item2.ToString("N2",CultureInfo.InvariantCulture)))
        .Concat([new(L["Daily_Counted"],Report.CountedCash?.ToString("N2",CultureInfo.InvariantCulture)??L["Daily_Uncounted"]),new(L["Daily_OpenShifts"],Report.OpenShifts.ToString(CultureInfo.InvariantCulture))]).ToList();
    public IReadOnlyList<PaymentTotalsItem> Methods=>Report.Methods.Select(r=>new PaymentTotalsItem(r)).ToList();
    public IReadOnlyList<SmartProfitItem> TopItems=>Report.TopItems.Select(r=>new SmartProfitItem(r)).ToList();
    public ICommand Print80Command{get;}public ICommand PrintA4Command{get;}public ICommand PdfCommand{get;}public ICommand CsvCommand{get;}
    public string Status{get;private set;}="";
    void Output(string kind)
    {
        try
        {
            var pages=DailyDocuments.Build(Report,Shell.S.Settings.GetValue("store_name")+" · "+CashierName,L.IsArabic,kind!="80",SmartReportsViewModel.DocumentLabels(L.IsArabic));
            if(kind is "pdf" or "csv")
            {
                var save=new Microsoft.Win32.SaveFileDialog{Filter=kind=="pdf"?"PDF|*.pdf":"CSV|*.csv",FileName=$"dukkan-daily-{Report.Date}.{kind}"};if(save.ShowDialog()!=true)return;
                if(kind=="pdf")PdfWriter.Save(save.FileName,pages);
                else CsvExport.Write(save.FileName,[L["Col_Date"],L["Col_Cashier"],L["Smart_Name"],L["Col_Total"]],Metrics.Select(r=>new[]{Report.Date,CashierName,r.Name,r.Value}).Concat(Methods.Select(r=>new[]{Report.Date,CashierName,L["Daily_Methods"]+" · "+r.Name,r.Net.ToString("0.00",CultureInfo.InvariantCulture)})).Concat(Report.TopItems.Select(r=>new[]{Report.Date,CashierName,L["Daily_Top"]+" · "+(string.IsNullOrWhiteSpace(r.NameAr)?r.NameEn:L.Pick(r.NameEn,r.NameAr)),r.Quantity.ToString("0.###",CultureInfo.InvariantCulture)})));
                Status=L["Toast_Exported"];
            }
            else
            {
                var printer=Shell.S.Settings.GetValue(kind=="80"?"printer_receipt":"printer_a4");if(string.IsNullOrWhiteSpace(printer)){Status=L["PO_SetPrinter"];Raise(nameof(Status));return;}
                Shell.S.Printer.Print(printer,pages,"Dukkan daily summary");Status=L["Print_Sent"];
            }
        }
        catch(Exception ex)when(ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or System.Printing.PrintSystemException){Status=L["Toast_CopyFailed"];}
        Raise(nameof(Status));
    }
}
