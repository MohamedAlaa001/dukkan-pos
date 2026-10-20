using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LocalCashier.Application;
using LocalCashier.Domain;

namespace Dukkan.UI;

public sealed class ManualCardEntry : ObservableObject
{
    string _approval="",_last4="";bool _confirmed;
    public string ApprovalCode {get=>_approval;set=>Set(ref _approval,value);}
    public string Last4 {get=>_last4;set=>Set(ref _last4,value);}
    public bool Confirmed {get=>_confirmed;set=>Set(ref _confirmed,value);}
    public bool IsValid=>PaymentTenderEdit.CardValid(Confirmed,ApprovalCode,Last4);
    public CardConfirmation Confirmation=>new(Confirmed,ApprovalCode,Last4);
}
public static class PaymentChoiceLists
{
    public static IReadOnlyList<PaymentMethodOption> Incoming(AppServices s)=>s.PaymentMethods.List().Where(m=>m.Kind!="credit").Select(m=>new PaymentMethodOption(m,true)).ToList();
    public static PaymentMethodOption First(IReadOnlyList<PaymentMethodOption> methods)=>methods.FirstOrDefault()??new PaymentMethodOption(new(1,"cash","Cash","نقدي","cash",false,10),true);
}

public sealed class PaymentTenderEdit : ObservableObject
{
    readonly Action _changed;
    PaymentMethodOption? _method;
    string _amount="",_reference="",_approval="",_last4="";
    bool _confirmed;
    public PaymentTenderEdit(IReadOnlyList<PaymentMethodOption> methods,Action changed)
    {Methods=methods;_method=methods.FirstOrDefault();_changed=changed;}
    public IReadOnlyList<PaymentMethodOption> Methods {get;}
    public PaymentMethodOption? Method {get=>_method;set {if(Set(ref _method,value)){_confirmed=false;_approval="";_last4="";_reference="";foreach(var p in new[]{nameof(IsCard),nameof(IsElectronic),nameof(ConfirmedOnTerminal),nameof(ApprovalCode),nameof(Last4),nameof(Reference)})Raise(p);_changed();}}}
    public bool IsCard=>Method?.Kind=="card";
    public bool IsElectronic=>Method?.Kind is "card" or "transfer";
    public string Amount {get=>_amount;set {if(Set(ref _amount,value)){if(IsCard){_confirmed=false;Raise(nameof(ConfirmedOnTerminal));}_changed();}}}
    public string Reference {get=>_reference;set {if(Set(ref _reference,value))_changed();}}
    public string ApprovalCode {get=>_approval;set {if(Set(ref _approval,value))_changed();}}
    public string Last4 {get=>_last4;set {if(Set(ref _last4,value))_changed();}}
    public bool ConfirmedOnTerminal {get=>_confirmed;set {if(Set(ref _confirmed,value))_changed();}}
    public decimal Value=>decimal.TryParse(Amount,NumberStyles.Number,CultureInfo.InvariantCulture,out var v)?v:-1;
    internal static bool CardValid(bool confirmed,string approval,string last)=>confirmed && approval.Trim().Length<=100 && (last.Trim().Length==0 || last.Trim().Length==4&&last.Trim().All(char.IsAsciiDigit));
    public bool IsValid=>Method is not null && Value>0 && Money.Round(Value)==Value && (!IsCard||CardValid(ConfirmedOnTerminal,ApprovalCode,Last4));
    public PaymentRequest ToRequest()
    {
        if(!IsValid)throw new PosException(PosError.PaymentMismatch,"Review tender amount and card confirmation.");
        return new(Method!.MethodKind,Value,Method.Id,string.IsNullOrWhiteSpace(Reference)?null:Reference.Trim(),IsCard?new(ConfirmedOnTerminal,ApprovalCode,Last4):null);
    }
}

public sealed partial class PaymentViewModel
{
    bool _split,_cardConfirmed;
    string _paymentReference="",_approvalCode="",_last4="";
    PaymentMethodOption? SelectedMethod=>Methods.FirstOrDefault(m=>m.Key==Method);
    bool CashEnabled=>Methods.Any(m=>m.Kind=="cash");
    public bool IsSimple=>!IsSplit;
    public bool IsSplit
    {
        get=>_split;
        set
        {
            if(!Set(ref _split,value))return;
            if(value && PaymentLines.Count==0 && TenderMethods.Count>0)PaymentLines.Add(new(TenderMethods,Recalculate){Amount=Total.ToString("0.##",CultureInfo.InvariantCulture)});
            foreach(var p in new[]{nameof(IsSimple),nameof(Width),nameof(IsCash),nameof(IsElectronic),nameof(IsCredit),nameof(ShowCashField),nameof(ShowCustomer)})Raise(p);
            Recalculate();
        }
    }
    public IReadOnlyList<PaymentMethodOption> TenderMethods {get;}
    public ObservableCollection<PaymentTenderEdit> PaymentLines {get;}=[];
    public ICommand AddPaymentCommand {get;}
    public ICommand RemovePaymentCommand {get;}
    public bool IsCard=>IsSimple && SelectedMethod?.Kind=="card";
    public string PaymentReference {get=>_paymentReference;set=>Set(ref _paymentReference,value);}
    public string ApprovalCode {get=>_approvalCode;set {if(Set(ref _approvalCode,value))Recalculate();}}
    public string Last4 {get=>_last4;set {if(Set(ref _last4,value))Recalculate();}}
    public bool ConfirmedOnTerminal {get=>_cardConfirmed;set {if(Set(ref _cardConfirmed,value))Recalculate();}}
    bool SingleCardValid=>PaymentTenderEdit.CardValid(ConfirmedOnTerminal,ApprovalCode,Last4);
    decimal SplitElectronic=>PaymentLines.Where(p=>p.Method?.Kind!="cash").Sum(p=>Math.Max(0,p.Value));
    decimal SplitCash=>PaymentLines.Where(p=>p.Method?.Kind=="cash").Sum(p=>Math.Max(0,p.Value));
    List<PaymentRequest> SimplePayments()
    {
        var payments=new List<PaymentRequest>();
        if(IsElectronic && ElectronicValue>0 && SelectedMethod is {} method)
            payments.Add(new(method.MethodKind,ElectronicValue,method.Id,string.IsNullOrWhiteSpace(PaymentReference)?null:PaymentReference.Trim(),IsCard?new(ConfirmedOnTerminal,ApprovalCode,Last4):null));
        if(ShowCashField && CashValue>0)payments.Add(new(PaymentMethod.Cash,CashValue,Methods.Single(m=>m.Kind=="cash").Id));
        return payments;
    }
}
