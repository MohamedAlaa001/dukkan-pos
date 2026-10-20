using System.Globalization;
using System.Windows.Input;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

public sealed record PromotionOfferItem(long Id,string NameEn,string NameAr,decimal Amount)
{public string Name=>Localizer.Instance.Pick(NameEn,NameAr);}

public sealed partial class PosViewModel
{
    PromotionQuote? _promotionQuote;
    string _coupon="",_promotionReason="",_pricingError="";
    long? _promotionApprover;
    readonly HashSet<long> _suppressedPromotions=[];
    public string Coupon {get=>_coupon;set {if(Set(ref _coupon,value))RaiseTotals();}}
    public string PricingError=>_pricingError;
    public bool HasPricingError=>_pricingError.Length>0;
    public decimal PromotionDiscount=>_promotionQuote is null?0:Money.FromCents(_promotionQuote.Evaluation.Discounts.Sum());
    public decimal Tax=>_promotionQuote?.Document.Tax??0;
    public bool HasTax=>Tax>0;
    public bool HasCustomer=>SelectedCustomer.Id>0;
    public long PointsBalance=>SelectedCustomer.Id>0?new LoyaltyService(_s.Db,_s.Clock).Balance(SelectedCustomer.Id):0;
    public IReadOnlyList<PromotionOfferItem> AppliedOffers=>_promotionQuote?.Evaluation.Applied.GroupBy(a=>a.PromotionId)
        .Select(g=>new PromotionOfferItem(g.Key,g.First().NameEn,g.First().NameAr,Money.FromCents(g.Sum(a=>a.AmountCents)))).ToList()??[];
    public ICommand RemoveOfferCommand=>new RelayCommand(p=>
    {
        var offer=(PromotionOfferItem)p!;
        _shell.ShowDialog(new ReasonViewModel(_shell,Localizer.Instance["Promo_Remove"],Localizer.Instance["Promo_RemoveHint"],reason=>
        {
            void Approved(long owner){_promotionApprover=owner;_promotionReason=reason;_suppressedPromotions.Add(offer.Id);_shell.CloseDialog();RefreshPromotionQuote(false);Raise(nameof(Total));}
            if(_shell.IsOwner)Approved(_shell.User!.Id);else _shell.ShowDialog(new ApprovalViewModel(_shell,Localizer.Instance["Promo_Remove"],Approved));
        }));
    });
    void RefreshPromotionQuote(bool reset=true)
    {
        if(reset){_suppressedPromotions.Clear();_promotionReason="";_promotionApprover=null;}
        _promotionQuote=null;_pricingError="";
        if(Cart.Count>0)
        {
            try{_promotionQuote=new PromotionService(_s.Db,_s.Clock).Preview(Cart.Select(c=>new SaleLineRequest(c.Variant.ProductId,c.Qty)).ToList(),_discount>0?LocalCashier.Domain.Discount.Fixed(_discount):null,
                SelectedCustomer.Id>0?SelectedCustomer.Id:null,Coupon,_suppressedPromotions.ToArray());}
            catch(PosException ex){_pricingError=_shell.ErrorText(ex);}
        }
        foreach(var name in new[]{nameof(PromotionDiscount),nameof(Tax),nameof(HasTax),nameof(HasCustomer),nameof(AppliedOffers),nameof(PointsBalance),nameof(PricingError),nameof(HasPricingError)})Raise(name);
    }
    internal void RefreshCurrentQuote(){RefreshPromotionQuote(false);Raise(nameof(Total));}
}

public sealed partial class PaymentViewModel
{
    string _redeemPoints="0";
    long _pointValueCents=1;
    public string RedeemPointsText
    {
        get=>_redeemPoints;
        set
        {
            var previousTotal=Total;
            if(!Set(ref _redeemPoints,value))return;
            _pointValueCents=new LoyaltyService(Shell.S.Db,Shell.S.Clock).Settings().ValueCents;
            var updated=Total;
            if(Parse(_cash)==previousTotal){_cash=updated.ToString("0.##",CultureInfo.InvariantCulture);Raise(nameof(CashText));}
            if(Parse(_electronic)==previousTotal){_electronic=updated.ToString("0.##",CultureInfo.InvariantCulture);Raise(nameof(ElectronicText));}
            _cardConfirmed=false;Raise(nameof(ConfirmedOnTerminal));
            foreach(var line in PaymentLines)line.ConfirmedOnTerminal=false;
            Recalculate();
        }
    }
    long RedeemPoints=>long.TryParse(_redeemPoints,NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)?n:-1;
    public long PointsBalance=>SelectedCustomer?.Id>0?new LoyaltyService(Shell.S.Db,Shell.S.Clock).Balance(SelectedCustomer.Id):0;
    public bool LoyaltyAvailable=>SelectedCustomer?.Id>0&&new LoyaltyService(Shell.S.Db,Shell.S.Clock).Settings().Enabled;
    public decimal RedeemedValue
    {
        get {var points=RedeemPoints;if(points<=0)return 0;return points>long.MaxValue/_pointValueCents?decimal.MaxValue:Money.FromCents(points*_pointValueCents);}
    }
    void RefreshLoyaltyQuote()
    {
        _pointValueCents=new LoyaltyService(Shell.S.Db,Shell.S.Clock).Settings().ValueCents;_cardConfirmed=false;Raise(nameof(ConfirmedOnTerminal));
        foreach(var line in PaymentLines)line.ConfirmedOnTerminal=false;Recalculate();
    }
    void ValidateLoyalty()
    {
        var config=new LoyaltyService(Shell.S.Db,Shell.S.Clock).Settings();var points=RedeemPoints;
        SetError(nameof(RedeemPointsText),points<0||points>0&&(!LoyaltyAvailable||points<config.MinimumRedeem||points>PointsBalance||RedeemedValue>_pos.Total)?Localizer.Instance["Loyalty_InvalidRedemption"]:null);
        foreach(var property in new[]{nameof(Total),nameof(PointsBalance),nameof(LoyaltyAvailable),nameof(RedeemedValue)})Raise(property);
    }
}
