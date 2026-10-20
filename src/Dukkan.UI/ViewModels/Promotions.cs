using System.Globalization;
using System.Windows.Input;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

public sealed record PromotionKindChoice(PromotionKind Kind)
{public override string ToString()=>Localizer.Instance["Promo_Kind_"+Kind];}
public sealed record PromotionListItem(PromotionDefinition Rule)
{
    public string Name=>Localizer.Instance.Pick(Rule.NameEn,Rule.NameAr);
    public string Kind=>new PromotionKindChoice(Rule.Kind).ToString();
    public string Status=>Localizer.Instance[Rule.Enabled?"Status_Active":"Status_Inactive"];
    public int Priority=>Rule.Priority;
    public string Coupon=>Rule.Coupon??"";
}

public sealed class PromotionWorkspaceViewModel : ObservableObject
{
    readonly ShellViewModel _shell;string _error="";
    int _tab;
    public int Tab {get=>_tab;set=>Set(ref _tab,value);}
    public PromotionWorkspaceViewModel(ShellViewModel shell)
    {
        _shell=shell;Reload();var config=new LoyaltyService(shell.S.Db,shell.S.Clock).Settings();
        Enabled=config.Enabled;EarnRate=config.EarnRate.ToString(CultureInfo.InvariantCulture);PointValue=Money.FromCents(config.ValueCents).ToString("0.00",CultureInfo.InvariantCulture);
        Minimum=config.MinimumRedeem.ToString(CultureInfo.InvariantCulture);ExpiryDays=config.ExpiryDays.ToString(CultureInfo.InvariantCulture);
        NewCommand=new RelayCommand(()=>Open(null),()=>shell.IsOwner);EditCommand=new RelayCommand(p=>Open(((PromotionListItem)p!).Rule),_=>shell.IsOwner);
        SaveConfigCommand=new RelayCommand(SaveConfig,()=>shell.IsOwner);
    }
    public IReadOnlyList<PromotionListItem> Items {get;private set;}=[];
    public bool Enabled {get;set;}
    public string EarnRate {get;set;}="0";
    public string PointValue {get;set;}="0.01";
    public string Minimum {get;set;}="1";
    public string ExpiryDays {get;set;}="0";
    public string Error {get=>_error;private set=>Set(ref _error,value);}
    public ICommand NewCommand {get;}
    public ICommand EditCommand {get;}
    public ICommand SaveConfigCommand {get;}
    void Reload(){Items=new PromotionService(_shell.S.Db,_shell.S.Clock).List().Select(r=>new PromotionListItem(r)).ToList();Raise(nameof(Items));}
    void Open(PromotionDefinition? rule)=>_shell.ShowDialog(new PromotionEditViewModel(_shell,rule,Reload));
    void SaveConfig()
    {
        try
        {
            var value=decimal.Parse(PointValue,NumberStyles.Number,CultureInfo.InvariantCulture);
            if(value!=Money.Round(value))throw new FormatException();
            new LoyaltyService(_shell.S.Db,_shell.S.Clock).Configure(_shell.User!.Id,new(Enabled,decimal.Parse(EarnRate,NumberStyles.Number,CultureInfo.InvariantCulture),Money.ToCents(value),long.Parse(Minimum,CultureInfo.InvariantCulture),int.Parse(ExpiryDays,CultureInfo.InvariantCulture)));
            Error="";_shell.Toast(Localizer.Instance["Toast_Saved"]);
        }
        catch(Exception ex) when(ex is FormatException or OverflowException){Error=Localizer.Instance["Err_Number"];}
        catch(PosException ex){Error=_shell.ErrorText(ex);}
    }
}

public sealed class PromotionProductPick(long id,string name) : ObservableObject
{
    public long Id {get;}=id;public string Name {get;}=name;
    public bool Selected {get;set;}
    public string Quantity {get;set;}="1";
}
public sealed class PromotionDayPick(int day,bool selected)
{
    public int Day {get;}=day;public bool Selected {get;set;}=selected;
    public string Name=>Localizer.Instance["Promo_Day"+Day];
}

public sealed class PromotionEditViewModel : DialogViewModel
{
    readonly PromotionDefinition? _original;readonly Action _changed;readonly Guid _operation=Guid.NewGuid();
    PromotionKindChoice _kind;string _search="",_error="";
    public PromotionEditViewModel(ShellViewModel shell,PromotionDefinition? rule,Action changed):base(shell)
    {
        _original=rule;_changed=changed;rule??=new(0,"","",PromotionKind.Percentage){Percent=10};
        Kinds=Enum.GetValues<PromotionKind>().Select(k=>new PromotionKindChoice(k)).ToList();_kind=Kinds.Single(k=>k.Kind==rule.Kind);
        NameEn=rule.NameEn;NameAr=rule.NameAr;Enabled=rule.Enabled;Stackable=rule.Stackable;Priority=rule.Priority.ToString(CultureInfo.InvariantCulture);
        Percent=rule.Percent.ToString(CultureInfo.InvariantCulture);Amount=Money.FromCents(rule.AmountCents).ToString("0.00",CultureInfo.InvariantCulture);Minimum=Money.FromCents(rule.MinimumCents).ToString("0.00",CultureInfo.InvariantCulture);
        Category=rule.Category??"";Group=rule.CustomerGroup??"";Coupon=rule.Coupon??"";Starts=rule.StartsOn??"";Ends=rule.EndsOn??"";
        StartTime=$"{rule.StartMinute/60:00}:{rule.StartMinute%60:00}";EndTime=$"{rule.EndMinute/60:00}:{rule.EndMinute%60:00}";
        Buy=rule.BuyQuantity.ToString(CultureInfo.InvariantCulture);Get=rule.GetQuantity.ToString(CultureInfo.InvariantCulture);SameProduct=rule.SameProduct;
        Days=Enumerable.Range(0,7).Select(d=>new PromotionDayPick(d,(rule.Weekdays&(1<<d))!=0)).ToList();
        Categories=["",..shell.S.Config.Categories().Select(c=>c.Name)];
        var picks=shell.S.Catalog.ListStock().Select(p=>new PromotionProductPick(p.ProductId,$"{p.Name} · {p.Sku}")
            {Selected=rule.ProductIds.Contains(p.ProductId)||rule.Bundle.Any(b=>b.ProductId==p.ProductId),Quantity=(rule.Bundle.FirstOrDefault(b=>b.ProductId==p.ProductId)?.Quantity??1).ToString(CultureInfo.InvariantCulture)}).ToList();
        foreach(var missing in rule.ProductIds.Concat(rule.Bundle.Select(b=>b.ProductId)).Distinct().Where(id=>!picks.Any(p=>p.Id==id)))
        {
            var product=shell.S.Catalog.GetById(missing);
            picks.Add(new(missing,$"{product?.Name??Localizer.Instance["Promo_UnavailableProduct"]} · {missing} · {Localizer.Instance["Status_Inactive"]}")
                {Selected=true,Quantity=(rule.Bundle.FirstOrDefault(b=>b.ProductId==missing)?.Quantity??1).ToString(CultureInfo.InvariantCulture)});
        }
        Products=picks;
        SaveCommand=new RelayCommand(Save,()=>shell.IsOwner);
    }
    public override double Width=>900;
    public IReadOnlyList<PromotionKindChoice> Kinds {get;}
    public PromotionKindChoice Kind {get=>_kind;set {if(Set(ref _kind,value)){Raise(nameof(HasPercent));Raise(nameof(HasAmount));Raise(nameof(HasMinimum));Raise(nameof(IsBuyGet));Raise(nameof(IsBundle));}}}
    public bool HasPercent=>Kind.Kind is PromotionKind.Percentage or PromotionKind.BuyGet;
    public bool HasAmount=>Kind.Kind is PromotionKind.FixedCart or PromotionKind.Bundle;
    public bool HasMinimum=>Kind.Kind==PromotionKind.FixedCart;
    public bool IsBuyGet=>Kind.Kind==PromotionKind.BuyGet;
    public bool IsBundle=>Kind.Kind==PromotionKind.Bundle;
    public string NameEn {get;set;}="";public string NameAr {get;set;}="";
    public bool Enabled {get;set;}public bool Stackable {get;set;}
    public string Priority {get;set;}="0";public string Percent {get;set;}="10";public string Amount {get;set;}="0";public string Minimum {get;set;}="0";
    public string Category {get;set;}="";public string Group {get;set;}="";public string Coupon {get;set;}="";
    public string Starts {get;set;}="";public string Ends {get;set;}="";public string StartTime {get;set;}="00:00";public string EndTime {get;set;}="00:00";
    public string Buy {get;set;}="2";public string Get {get;set;}="1";public bool SameProduct {get;set;}
    public IReadOnlyList<PromotionDayPick> Days {get;}public IReadOnlyList<string> Categories {get;}
    public IReadOnlyList<PromotionProductPick> Products {get;}
    public IReadOnlyList<PromotionProductPick> VisibleProducts=>Products.Where(p=>string.IsNullOrWhiteSpace(Search)||p.Name.Contains(Search,StringComparison.CurrentCultureIgnoreCase)).ToList();
    public string Search {get=>_search;set {if(Set(ref _search,value))Raise(nameof(VisibleProducts));}}
    public string Error {get=>_error;private set=>Set(ref _error,value);}
    public ICommand SaveCommand {get;}
    void Save()
    {
        if(string.IsNullOrWhiteSpace(NameEn)||string.IsNullOrWhiteSpace(NameAr))
        {Error=Localizer.Instance["Promo_NamesRequired"];return;}
        try
        {
            static decimal Number(string text)=>decimal.Parse(text,NumberStyles.Number,CultureInfo.InvariantCulture);
            static long Cents(string text){var n=Number(text);if(n!=Money.Round(n))throw new FormatException();return Money.ToCents(n);}
            static int Minute(string text){var t=TimeOnly.ParseExact(text,"HH:mm",CultureInfo.InvariantCulture);return t.Hour*60+t.Minute;}
            var selected=Products.Where(p=>p.Selected).ToList();
            var rule=new PromotionDefinition(_original?.Id??0,NameEn,NameAr,Kind.Kind)
            {
                Enabled=Enabled,Stackable=Stackable,Priority=int.Parse(Priority,CultureInfo.InvariantCulture),Percent=HasPercent?Number(Percent):0,
                AmountCents=HasAmount?Cents(Amount):0,MinimumCents=HasMinimum?Cents(Minimum):0,Category=IsBundle||Kind.Kind==PromotionKind.FixedCart?null:Category,
                CustomerGroup=Group,Coupon=Coupon,StartsOn=Starts,EndsOn=Ends,StartMinute=Minute(StartTime),EndMinute=Minute(EndTime),Weekdays=Days.Where(d=>d.Selected).Sum(d=>1<<d.Day),
                BuyQuantity=IsBuyGet?int.Parse(Buy,CultureInfo.InvariantCulture):2,GetQuantity=IsBuyGet?int.Parse(Get,CultureInfo.InvariantCulture):1,SameProduct=SameProduct,
                ProductIds=Kind.Kind==PromotionKind.FixedCart?[]:selected.Select(p=>p.Id).ToList(),
                Bundle=IsBundle?selected.Select(p=>new BundleRequirement(p.Id,int.Parse(p.Quantity,CultureInfo.InvariantCulture))).ToList():[]
            };
            new PromotionService(Shell.S.Db,Shell.S.Clock).Save(_operation,Shell.User!.Id,rule);_changed();Shell.CloseDialog();Shell.Toast(Localizer.Instance["Toast_Saved"]);
        }
        catch(Exception ex) when(ex is FormatException or OverflowException){Error=Localizer.Instance["Err_Number"];}
        catch(PosException ex){Error=Shell.ErrorText(ex);}
    }
}

public sealed record LoyaltyEntryItem(LoyaltyEntry Row)
{
    public string Kind=>Localizer.Instance["Loyalty_"+Row.Kind];public long Points=>Row.Points;
    public string Date=>DateTimeOffset.Parse(Row.CreatedAt,CultureInfo.InvariantCulture).ToString("dd/MM/yyyy HH:mm",CultureInfo.InvariantCulture);
    public string Expiry=>Row.ExpiresOn??"";
}
public sealed class CustomerLoyaltyViewModel : ObservableObject
{
    readonly ShellViewModel _shell;readonly long _customer;string _error="";
    public CustomerLoyaltyViewModel(ShellViewModel shell,long customer)
    {
        _shell=shell;_customer=customer;Group=new PromotionService(shell.S.Db,shell.S.Clock).CustomerGroup(customer);Reload();
        SaveGroupCommand=new RelayCommand(()=>
        {
            try{new PromotionService(shell.S.Db,shell.S.Clock).SetCustomerGroup(Guid.NewGuid(),shell.User!.Id,customer,Group);Error="";shell.Toast(Localizer.Instance["Toast_Saved"]);}
            catch(PosException ex){Error=shell.ErrorText(ex);}
        },()=>shell.IsOwner);
        AdjustCommand=new RelayCommand(()=>shell.ShowDialog(new LoyaltyAdjustViewModel(shell,customer,Reload)),()=>shell.IsOwner);
    }
    public bool IsOwner=>_shell.IsOwner;
    public long Balance {get;private set;}
    public string Group {get;set;}="";
    public IReadOnlyList<LoyaltyEntryItem> Entries {get;private set;}=[];
    public string Error {get=>_error;private set=>Set(ref _error,value);}
    public ICommand SaveGroupCommand {get;}public ICommand AdjustCommand {get;}
    void Reload()
    {
        var service=new LoyaltyService(_shell.S.Db,_shell.S.Clock);service.ExpireDue(_customer);Balance=service.Balance(_customer);Entries=service.History(_customer).Select(e=>new LoyaltyEntryItem(e)).ToList();Raise(nameof(Balance));Raise(nameof(Entries));
    }
}
public sealed class LoyaltyAdjustViewModel : DialogViewModel
{
    readonly Guid _operation=Guid.NewGuid();string _error="";
    public LoyaltyAdjustViewModel(ShellViewModel shell,long customer,Action changed):base(shell)
    {
        SaveCommand=new RelayCommand(()=>
        {
            if(!long.TryParse(Points,NumberStyles.Integer,CultureInfo.InvariantCulture,out var points)){Error=Localizer.Instance["Err_Number"];return;}
            try{new LoyaltyService(shell.S.Db,shell.S.Clock).Adjust(_operation,shell.User!.Id,customer,points,Reason);changed();shell.CloseDialog();}
            catch(PosException ex){Error=shell.ErrorText(ex);}
        },()=>shell.IsOwner);
    }
    public override double Width=>500;
    public string Points {get;set;}="";public string Reason {get;set;}="";
    public string Error {get=>_error;private set=>Set(ref _error,value);}
    public ICommand SaveCommand {get;}
}
