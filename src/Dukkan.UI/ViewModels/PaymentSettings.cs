using System.Globalization;
using System.Windows.Input;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

public sealed class PaymentMethodEdit(PaymentMethodRow row) : ObservableObject
{
    public long Id=>row.Id;
    public string Code=>row.Code;
    public string Kind=>Localizer.Instance["Pay_Kind_"+row.Kind];
    public string NameEn {get;set;}=row.NameEn;
    public string NameAr {get;set;}=row.NameAr;
    public bool Enabled {get;set;}=row.Enabled;
    public string Sort {get;set;}=row.SortOrder.ToString(CultureInfo.InvariantCulture);
}
public sealed class PaymentSettingsViewModel : ObservableObject
{
    readonly ShellViewModel _shell;string _error="";
    public PaymentSettingsViewModel(ShellViewModel shell)
    {
        _shell=shell;Rows=shell.S.PaymentMethods.List(false).Select(r=>new PaymentMethodEdit(r)).ToList();SaveCommand=new RelayCommand(p=>Save((PaymentMethodEdit)p!));
    }
    public IReadOnlyList<PaymentMethodEdit> Rows {get;}
    public string Error {get=>_error;private set=>Set(ref _error,value);}
    public ICommand SaveCommand {get;}
    void Save(PaymentMethodEdit row)
    {
        if(!int.TryParse(row.Sort,NumberStyles.Integer,CultureInfo.InvariantCulture,out var sort)||sort<0){Error=Localizer.Instance["Err_Number"];return;}
        try{_shell.S.PaymentMethods.Update(_shell.User!.Id,row.Id,row.NameEn,row.NameAr,row.Enabled,sort);Error="";_shell.Toast(Localizer.Instance["Toast_Saved"]);}
        catch(PosException ex){Error=_shell.ErrorText(ex);}
    }
}
