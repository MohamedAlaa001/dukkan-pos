using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace Dukkan.UI;

public sealed record StockProductChoice(long Id, string Name, string Category)
{ public override string ToString() => Name; }
public sealed class CountEntryItem(StockCountLineRow row) : ObservableObject
{
    string _count = row.Counted?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    public StockCountLineRow Row { get; } = row;
    public string Count { get => _count; set => Set(ref _count, value); }
}

/// <summary>Locations, stocktake and optional lot control. Cashiers only see count entry.</summary>
public sealed class StockWorkspaceViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    StockLocationRow? _location, _destination;
    StockProductChoice? _product;
    StockLotRow? _lot;
    CountSessionRow? _session;
    SupplierItem? _supplier;
    string _quantity="", _cost="", _reason="", _batch="", _expiry="", _point="0", _target="0", _name="", _days="30", _error="";
    bool _track;
    int _tab;
    public StockWorkspaceViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Products = shell.S.Styles.List().SelectMany(s => shell.S.Styles.Variants(s.Id).Where(v => v.Active)
            .Select(v => new StockProductChoice(v.ProductId, string.Join(" · ", new[]{ Localizer.Instance.Pick(s.NameEn,s.NameAlt??s.NameEn),v.Size,v.ColorNameEn }.Where(x=>!string.IsNullOrWhiteSpace(x))),s.CategoryKey))).ToList();
        Suppliers = shell.IsOwner ? shell.S.Suppliers.List().Where(s=>s.Active).Select(s=>new SupplierItem(s)).ToList() : [];
        Locations = shell.S.Stock.Locations(); _location = Locations.FirstOrDefault(); _destination = Locations.LastOrDefault();
        _days = shell.S.Settings.GetValue("expiry_warning_days") ?? "30";
        ReloadCommand=Command(Reload);
        ReceiveCommand=new RelayCommand(()=>shell.ShowDialog(new StockAdjustViewModel(shell,Reload)),()=>IsOwner);
        HistoryCommand=new RelayCommand(()=>shell.ShowPage(new InventoryViewModel(shell)),()=>IsOwner);
        CreateLocationCommand=Command(()=>{ shell.S.Stock.CreateLocation(User,Name); RefreshLocations(); Name=""; });
        SalesLocationCommand=Command(()=>shell.S.Stock.SetSalesLocation(User,Location!.Id));
        TransferCommand=Command(()=>{ shell.S.Stock.Transfer(Guid.NewGuid(),User,Product!.Id,Location!.Id,Destination!.Id,Number(Quantity),Lot?.Id); Quantity=""; Reload(); });
        AdjustCommand=Command(()=>{ shell.S.Stock.AdjustStock(Guid.NewGuid(),User,Product!.Id,Location!.Id,Lot?.Id,Number(Quantity),string.IsNullOrWhiteSpace(Cost)?null:Number(Cost),Reason); Quantity=""; Reload(); });
        ConfigureCommand=Command(()=>{ shell.S.Stock.ConfigureProduct(User,Product!.Id,TrackExpiry,Number(Point),Number(Target),Supplier?.Id,Batch,Expiry); LoadProduct(); Reload(); });
        ClassifyCommand=Command(()=>{shell.S.Stock.ClassifyLot(Guid.NewGuid(),User,Product!.Id,Location!.Id,Number(Quantity),Batch,Expiry);Quantity="";LoadProduct();Reload();});
        DefineLotCommand=Command(()=>{var id=shell.S.Stock.DefineLot(User,Product!.Id,Batch,Expiry);LoadProduct();Lot=Lots.Single(l=>l.Id==id);});
        WarningCommand=Command(()=>{ if(!int.TryParse(Days,out var days)) throw new PosException(PosError.InvalidQuantity,"Days"); shell.S.Stock.SetExpiryWarningDays(User,days); Reload(); });
        CreateCountCommand=new RelayCommand(()=>shell.ShowDialog(new CountOpenViewModel(shell,Location!.Id,Product?.Id,id=>{Reload();Session=Sessions.Single(s=>s.Id==id);})),()=>IsOwner);
        SaveCountCommand=Command(()=>
        {
            var filled=CountLines.Where(l=>l.Count.Trim().Length>0).ToList();
            if(filled.Count==0 || filled.Any(l=>Number(l.Count)<0)) throw new PosException(PosError.InvalidQuantity,"Count");
            var entered=filled.Where(l=>l.Row.Counted!=Number(l.Count)).ToList();
            foreach(var line in entered) shell.S.Stock.RecordCount(Guid.NewGuid(),User,line.Row.Id,Number(line.Count));
            LoadCount(); shell.Toast(L["Stock_CountSaved"]);
        });
        RecountCommand=new RelayCommand(p=>
        {
            Error="";
            try {var line=(CountEntryItem)p!;shell.S.Stock.RecordCount(Guid.NewGuid(),User,line.Row.Id,Number(line.Count));LoadCount();}
            catch(PosException ex){Error=shell.ErrorText(ex);}
            catch(Exception ex) when(ex is FormatException or OverflowException){Error=L["Stock_Invalid"];}
        });
        ApproveCountCommand=Command(()=>
        {
            if(string.IsNullOrWhiteSpace(Reason)){Error=L["Stock_ApprovalReason"];return;}
            if(CountLines.Any(l=>l.Count.Trim().Length==0?l.Row.Counted is not null:l.Row.Counted!=Number(l.Count)))
                throw new PosException(PosError.StockCountUnsaved,"Save and review count entries before approval.");
            shell.S.Stock.ApproveCount(Guid.NewGuid(),User,Session!.Id,Reason); Reload();
        });
        CancelCountCommand=Command(()=>{shell.S.Stock.CancelCount(Guid.NewGuid(),User,Session!.Id,Reason);Reload();});
        ReorderCommand=Command(()=>
        {
            var id=shell.S.Stock.CreateReorderOrder(Guid.NewGuid(),User,Supplier!.Id);
            shell.ShowDialog(new PurchaseOrderDetailsViewModel(shell,shell.S.Suppliers.OrderList().Single(o=>o.Id==id),Reload));
        });
        ExportCommand=new RelayCommand(()=>Output(false)); PrintCommand=new RelayCommand(()=>Output(true));
        ClearProductCommand=new RelayCommand(()=>Product=null);
        Reload();
    }
    long User=>_shell.User!.Id;
    public Localizer L=>Localizer.Instance;
    public bool IsOwner=>_shell.IsOwner;
    public bool IsCashier=>!IsOwner;
    public int Tab { get=>_tab; set=>Set(ref _tab,value); }
    public string Error { get=>_error; private set=>Set(ref _error,value); }
    public IReadOnlyList<StockProductChoice> Products { get; }
    public IReadOnlyList<SupplierItem> Suppliers { get; }
    public IReadOnlyList<StockLocationRow> Locations { get; private set; }
    public IReadOnlyList<StockLotRow> Lots { get; private set; }=[];
    public IReadOnlyList<StockLocationLevel> Levels { get; private set; }=[];
    public IReadOnlyList<ExpiryStockRow> Expiring { get; private set; }=[];
    public IReadOnlyList<ReorderSuggestion> Suggestions { get; private set; }=[];
    public IReadOnlyList<CountSessionRow> Sessions { get; private set; }=[];
    public ObservableCollection<CountEntryItem> CountLines { get; }=[];
    public string SalesLocationName=>Locations.FirstOrDefault(l=>l.Id==_shell.S.Stock.SalesLocation)?.Name??"";
    public StockLocationRow? Location { get=>_location; set {if(Set(ref _location,value)) Reload();} }
    public StockLocationRow? Destination { get=>_destination; set=>Set(ref _destination,value); }
    public StockProductChoice? Product { get=>_product; set {if(Set(ref _product,value)) LoadProduct();} }
    public StockLotRow? Lot { get=>_lot; set=>Set(ref _lot,value); }
    public SupplierItem? Supplier { get=>_supplier; set=>Set(ref _supplier,value); }
    public CountSessionRow? Session { get=>_session; set {if(Set(ref _session,value)) LoadCount();} }
    public bool CountOpen=>Session?.State=="open";
    public string SessionStatus=>Session is null?"":L[Session.State switch {"open"=>"Stock_Open","approved"=>"Stock_Approved",_=>"Stock_Cancelled"}];
    public decimal VarianceValue=>CountLines.Sum(l=>l.Row.VarianceValue);
    public string Quantity {get=>_quantity;set=>Set(ref _quantity,value);}
    public string Cost {get=>_cost;set=>Set(ref _cost,value);}
    public string Reason {get=>_reason;set=>Set(ref _reason,value);}
    public string Batch {get=>_batch;set=>Set(ref _batch,value);}
    public string Expiry {get=>_expiry;set=>Set(ref _expiry,value);}
    public string Point {get=>_point;set=>Set(ref _point,value);}
    public string Target {get=>_target;set=>Set(ref _target,value);}
    public string Name {get=>_name;set=>Set(ref _name,value);}
    public string Days {get=>_days;set=>Set(ref _days,value);}
    public bool TrackExpiry {get=>_track;set=>Set(ref _track,value);}
    public ICommand ReloadCommand {get;} public ICommand ReceiveCommand {get;} public ICommand HistoryCommand {get;}
    public ICommand CreateLocationCommand {get;} public ICommand SalesLocationCommand {get;}
    public ICommand TransferCommand {get;} public ICommand AdjustCommand {get;} public ICommand ConfigureCommand {get;} public ICommand WarningCommand {get;}
    public ICommand CreateCountCommand {get;} public ICommand SaveCountCommand {get;} public ICommand ApproveCountCommand {get;}
    public ICommand ReorderCommand {get;} public ICommand ExportCommand {get;} public ICommand PrintCommand {get;}
    public ICommand ClearProductCommand {get;}
    public ICommand ClassifyCommand {get;}
    public ICommand CancelCountCommand {get;}
    public ICommand DefineLotCommand {get;}
    public ICommand RecountCommand {get;}
    RelayCommand Command(Action action)=>new(()=>
    {
        Error="";
        try { action(); Raise(nameof(SalesLocationName)); }
        catch(PosException ex) {Error=_shell.ErrorText(ex);}
        catch(Exception ex) when(ex is FormatException or OverflowException or NullReferenceException) { Error=L["Stock_Invalid"]; }
    });
    static decimal Number(string value)=>decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out var n)?n:throw new FormatException();
    void RefreshLocations()
    {
        var id=Location?.Id; Locations=_shell.S.Stock.Locations(); Raise(nameof(Locations));
        Location=Locations.FirstOrDefault(l=>l.Id==id)??Locations.First(); Destination=Locations.Last();
    }
    void LoadProduct()
    {
        Lots=Product is null?[]:_shell.S.Stock.Lots(Product.Id); Raise(nameof(Lots)); Lot=null;
        if(Product is null)return;
        var cfg=_shell.S.Stock.ProductConfig(Product.Id); TrackExpiry=cfg.TrackExpiry; Point=cfg.ReorderPoint.ToString(CultureInfo.InvariantCulture); Target=cfg.TargetLevel.ToString(CultureInfo.InvariantCulture);
        Supplier=Suppliers.FirstOrDefault(s=>s.Id==cfg.SupplierId); Cost=(SupplierVariantLookup.Get(_shell,Product.Id)?.AvgCost??0m).ToString("0.####",CultureInfo.InvariantCulture);
    }
    void Reload()
    {
        if(IsOwner) { Levels=_shell.S.Stock.Levels(Location?.Id).Select(r=>r with {Product=ProductText(r.ProductId,r.Product)}).ToList(); Expiring=_shell.S.Stock.NearExpiry().Select(r=>r with {Product=ProductText(r.ProductId,r.Product)}).ToList(); Suggestions=_shell.S.Stock.ReorderSuggestions().Select(r=>r with {Product=ProductText(r.ProductId,r.Product)}).ToList(); Raise(nameof(Levels)); Raise(nameof(Expiring)); Raise(nameof(Suggestions)); }
        var id=Session?.Id; Sessions=_shell.S.Stock.CountSessions(); Raise(nameof(Sessions)); Session=Sessions.FirstOrDefault(s=>s.Id==id)??Sessions.FirstOrDefault();
        LoadCount(); Raise(nameof(SalesLocationName));
        _shell.RefreshNotifications();
    }
    void LoadCount()
    {
        CountLines.Clear(); if(Session is not null)foreach(var row in _shell.S.Stock.CountLines(Session.Id))CountLines.Add(new(row with {Product=ProductText(row.ProductId,row.Product)}));
        Raise(nameof(CountOpen)); Raise(nameof(VarianceValue)); Raise(nameof(SessionStatus));
    }
    string ProductText(long productId,string fallback)=>Products.FirstOrDefault(p=>p.Id==productId)?.Name??fallback;
    void Output(bool print)
    {
        if(Session is null)return;
        try
        {
            if(print)
            {
                var labels=new[]{"Stock_Counts","Stock_Location","Col_Product","Stock_Batch","Stock_Counted","Stock_Expected"}.ToDictionary(k=>k,k=>L[k]);
                var pages=StockDocuments.CountSheet(Session,CountLines.Select(l=>l.Row).ToList(),L.IsArabic,labels);
                var printer=_shell.S.Settings.GetValue("printer_a4");
                if(string.IsNullOrWhiteSpace(printer)){_shell.Toast(L["PO_SetPrinter"],BadgeKind.Warning);return;}
                _shell.S.Printer.Print(printer,pages,"Dukkan stock count");
            }
            else
            {
                var save=new Microsoft.Win32.SaveFileDialog { Filter="CSV|*.csv",FileName=$"dukkan-count-{Session.Id}.csv" };
                if(save.ShowDialog()!=true)return;
                var headers=new List<string>{L["Col_Product"],L["Stock_Batch"],L["Stock_Expected"],L["Stock_Counted"],L["Stock_Variance"]}; if(IsOwner)headers.Add(L["Stock_VarianceValue"]);
                CsvExport.Write(save.FileName,headers,CountLines.Select(l=>
                {
                    var cells=new List<string>{l.Row.Product,l.Row.Batch??"",l.Row.Expected.ToString(CultureInfo.InvariantCulture),l.Row.Counted?.ToString(CultureInfo.InvariantCulture)??"",l.Row.Variance.ToString(CultureInfo.InvariantCulture)};
                    if(IsOwner)cells.Add(l.Row.VarianceValue.ToString("0.00",CultureInfo.InvariantCulture)); return cells.ToArray();
                }));
                _shell.Toast(L["Toast_Exported"]);
            }
        }
        catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException){Error=L["Toast_CopyFailed"];}
    }
}

public sealed record CountCategoryChoice(string Key,string Name) {public override string ToString()=>Name;}
public sealed class CountProductSelect(StockProductChoice product) : ObservableObject
{
    bool _selected;
    public StockProductChoice Product {get;}=product;
    public bool Selected {get=>_selected;set=>Set(ref _selected,value);}
}
public sealed class CountOpenViewModel : DialogViewModel
{
    readonly Guid _operation=Guid.NewGuid();readonly Action<long> _done;
    Option _scope;string _search="",_error="";
    public CountOpenViewModel(ShellViewModel shell,long locationId,long? productId,Action<long> done):base(shell)
    {
        _done=done;Locations=shell.S.Stock.Locations();Location=Locations.Single(l=>l.Id==locationId);
        Products=shell.S.Styles.List().SelectMany(s=>shell.S.Styles.Variants(s.Id).Where(v=>v.Active).Select(v=>new CountProductSelect(new(v.ProductId,
            string.Join(" · ",new[]{Localizer.Instance.Pick(s.NameEn,s.NameAlt??s.NameEn),v.Size,v.ColorNameEn}.Where(x=>!string.IsNullOrWhiteSpace(x))),s.CategoryKey)) {Selected=v.ProductId==productId})).ToList();
        Categories=Products.Select(p=>p.Product.Category).Distinct().Order().Select(c=>new CountCategoryChoice(c,CategoryNames.Label(c))).ToList();Category=Categories.FirstOrDefault();
        Scopes=new[]{"Stock_Scope_All","Stock_Scope_Category","Stock_Scope_Selected"}.Select(k=>new Option(k)).ToList();_scope=Scopes[productId is null?0:2];
        SaveCommand=new RelayCommand(Save);
    }
    public override double Width=>640;
    public IReadOnlyList<StockLocationRow> Locations {get;}
    public StockLocationRow Location {get;set;}
    public IReadOnlyList<Option> Scopes {get;}
    public Option Scope {get=>_scope;set {if(Set(ref _scope,value)){Raise(nameof(IsCategory));Raise(nameof(IsSelection));}}}
    public bool IsCategory=>Scope.Key=="Stock_Scope_Category";
    public bool IsSelection=>Scope.Key=="Stock_Scope_Selected";
    public IReadOnlyList<CountCategoryChoice> Categories {get;}
    public CountCategoryChoice? Category {get;set;}
    public IReadOnlyList<CountProductSelect> Products {get;}
    public IEnumerable<CountProductSelect> VisibleProducts=>Products.Where(p=>p.Product.Name.Contains(Search,StringComparison.CurrentCultureIgnoreCase)).Take(200);
    public string Search {get=>_search;set {if(Set(ref _search,value))Raise(nameof(VisibleProducts));}}
    public string Error {get=>_error;private set=>Set(ref _error,value);}
    public ICommand SaveCommand {get;}
    void Save()
    {
        try
        {
            IReadOnlyList<long>? ids=IsCategory?Products.Where(p=>p.Product.Category==Category?.Key).Select(p=>p.Product.Id).ToList():IsSelection?Products.Where(p=>p.Selected).Select(p=>p.Product.Id).ToList():null;
            var id=Shell.S.Stock.CreateCount(_operation,Shell.User!.Id,Location.Id,ids);Shell.CloseDialog();_done(id);
        }
        catch(PosException ex){Error=Shell.ErrorText(ex);}
    }
}
