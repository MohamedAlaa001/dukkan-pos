using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace Dukkan.UI;

// ===================================================================== POS screen
public sealed partial class PosViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    readonly AppServices _s;
    string _search = "";
    string _category = "All";
    CustomerItem _customer;
    decimal _discount;
    long? _discountApprover;

    public PosViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _s = shell.S;
        Categories = ["All", .. shell.S.Config.Categories().Select(c => c.Name)];
        Cart = [];
        Cart.CollectionChanged += (_, _) => RaiseTotals();
        Customers = [new CustomerItem(new CustomerRow(0, Localizer.Instance["Inv_WalkIn"], null, false, 0m, 0m)), .. _s.Customers.List().Select(c => new CustomerItem(c))];
        _customer = Customers[0];
        AddStyleCommand = new RelayCommand(p => AddStyle((StyleItem)p!));
        SubmitSearchCommand = new RelayCommand(SubmitSearch);
        RemoveCommand = new RelayCommand(p => Cart.Remove((CartLine)p!));
        IncreaseCommand = new RelayCommand(p => ChangeQty((CartLine)p!, +1));
        DecreaseCommand = new RelayCommand(p => ChangeQty((CartLine)p!, -1));
        SelectCategoryCommand = new RelayCommand(p => Category = (string)p!);
        ClearCommand = new RelayCommand(Clear, () => Cart.Count > 0);
        HoldCommand = new RelayCommand(Hold, () => Cart.Count > 0);
        ShowHeldCommand = new RelayCommand(() => _shell.ShowDialog(new HeldCartsViewModel(_shell, this)));
        DiscountCommand = new RelayCommand(OpenDiscount, () => Cart.Count > 0);
        PayCommand = new RelayCommand(Pay, () => Cart.Count > 0&&!HasPricingError);
        Localizer.Instance.PropertyChanged += (_, _) => { Raise(nameof(Products)); Raise(nameof(HoldText)); Raise(nameof(HeldButtonText)); };
        Reload();
    }

    public Localizer L => Localizer.Instance;
    public ShellViewModel Shell => _shell;
    public string HeldButtonText => HeldCount > 0 ? $"{L["Pos_HeldCarts"]} ({HeldCount})" : L["Pos_HeldCarts"];
    public IReadOnlyList<string> Categories { get; private set; }
    public ObservableCollection<CartLine> Cart { get; }
    public IReadOnlyList<CustomerItem> Customers { get; private set; }
    public IReadOnlyList<StyleItem> Products { get; private set; } = [];

    /// <summary>Picks up customers added at checkout so the next sale can select them.</summary>
    public void ReloadCustomers()
    {
        var customerId = _customer.Id;
        Customers = [Customers[0], .. _s.Customers.List().Select(c => new CustomerItem(c))];
        Raise(nameof(Customers));
        SelectedCustomer = Customers.FirstOrDefault(c => c.Id == customerId) ?? Customers[0];
    }

    /// <summary>Refresh shop data on returning to the retained cart from another workspace.</summary>
    public void RefreshCatalog()
    {
        Categories = ["All", .. _s.Config.Categories().Select(c => c.Name)];
        Raise(nameof(Categories));
        if (!Categories.Contains(_category)) { _category = "All"; Raise(nameof(Category)); }
        ReloadCustomers();
        Reload();
        Raise(nameof(HeldButtonText));
        Raise(nameof(HasOpenShift));
    }
    public ICommand AddStyleCommand { get; }
    public ICommand SubmitSearchCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand IncreaseCommand { get; }
    public ICommand DecreaseCommand { get; }
    public ICommand SelectCategoryCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand HoldCommand { get; }
    public ICommand ShowHeldCommand { get; }
    public ICommand DiscountCommand { get; }
    public ICommand PayCommand { get; }

    public string Search { get => _search; set { if (Set(ref _search, value)) Reload(); } }
    public string Category { get => _category; set { if (Set(ref _category, value)) Reload(); } }
    public CustomerItem SelectedCustomer { get => _customer; set {if(Set(ref _customer,value??Customers[0]))RaiseTotals();} }
    public int HeldCount => _s.Held.List().Count;
    public string HoldText => HeldCount > 0 ? $"{L["Pos_Hold"]} ({HeldCount})" : L["Pos_Hold"];
    public decimal Subtotal => Cart.Sum(c => c.Total);
    public decimal Discount => _discount;
    public decimal Total => _promotionQuote?.Document.Total??Math.Max(0, Subtotal - _discount);
    public decimal ItemCount => Cart.Sum(c => c.Qty);
    public bool IsEmptyCart => Cart.Count == 0;
    public bool HasOpenShift => _shell.HasShift;

    void Reload()
    {
        var cat = _category == "All" ? null : _category;
        Products = _s.Styles.List(string.IsNullOrWhiteSpace(_search) ? null : _search.Trim(), cat, locationId:_s.Stock.SalesLocation).Select(r => new StyleItem(r)).Take(80).ToList();
        Raise(nameof(Products));
    }

    void RaiseTotals()
    {
        // A discount only stays valid while the cart it was approved for is unchanged enough to cover it.
        if (_discount > Subtotal) { _discount = 0; _discountApprover = null; }
        RefreshPromotionQuote();
        Raise(nameof(Subtotal)); Raise(nameof(Total)); Raise(nameof(ItemCount)); Raise(nameof(Discount)); Raise(nameof(IsEmptyCart));
        CommandManager.InvalidateRequerySuggested();
    }

    public IReadOnlyList<VariantItem> VariantsOf(StyleItem style) =>
        _s.Styles.Variants(style.Id,_s.Stock.SalesLocation).Where(v => v.Active).Select(v => new VariantItem(v, style.NameEn, style.NameAlt, style.Kind)).ToList();

    void AddStyle(StyleItem style)
    {
        var variants = VariantsOf(style);
        if (variants.Count == 0) { _shell.Toast(L["Toast_OutOfStock"], BadgeKind.Danger); return; }
        if (variants.Count == 1) AddVariant(variants[0]);
        else _shell.ShowDialog(new VariantPickerViewModel(_shell, style, variants, AddVariant));
    }

    public void AddVariant(VariantItem v)
    {
        var existing = Cart.FirstOrDefault(c => c.Variant.ProductId == v.ProductId);
        var wanted = (existing?.Qty ?? 0) + 1;
        if (wanted > v.Stock)
        {
            _shell.Toast(string.Format(L["Toast_OnlyN"], v.Stock, v.FullName), BadgeKind.Warning);
            return;
        }

        if (existing is not null) existing.Qty = wanted;
        else Cart.Add(new CartLine { Variant = v, Qty = 1, Edited = RaiseTotals });
        RaiseTotals();
    }

    void ChangeQty(CartLine line, int delta)
    {
        if (delta > 0 && line.Qty + 1 > line.Variant.Stock) { _shell.Toast(string.Format(L["Toast_OnlyN"], line.Variant.Stock, line.Variant.FullName), BadgeKind.Warning); return; }
        if (delta < 0 && line.Qty <= 1) return;
        line.Qty += delta;
        RaiseTotals();
    }

    /// <summary>Enter in the search box: an exact barcode adds that variant; otherwise a single match is added, several are filtered.</summary>
    void SubmitSearch()
    {
        var text = _search.Trim();
        if (text.Length == 0) return;
        var hit = _s.Catalog.FindByBarcode(text);
        if (hit is not null)
        {
            if (!hit.Active) { _shell.Toast(L["Toast_OutOfStock"], BadgeKind.Danger); return; }
            var style = _s.Styles.Get(hit.StyleId);
            var row = _s.Styles.Variants(hit.StyleId,_s.Stock.SalesLocation).First(v => v.ProductId == hit.Id);
            AddVariant(new VariantItem(row, style!.Input.NameEn, style.Input.NameAlt, Enum.TryParse<GarmentKind>(style.Input.Kind, out var k) ? k : GarmentKind.TShirt));
            Search = "";
            return;
        }

        if (text.All(char.IsAsciiDigit) && text.Length >= 8) { _shell.Toast(L["Toast_UnknownBarcode"], BadgeKind.Warning); return; }   // never auto-create products from a scan
        if (Products.Count == 1) { AddStyle(Products[0]); Search = ""; }
    }

    void Clear() => _shell.Confirm(L["Pos_Clear"], L["Confirm_ClearCart"], L["Pos_Clear"], true, ClearCart);

    public void ClearCart()
    {
        Cart.Clear();
        _discount = 0; _discountApprover = null;
        _coupon="";Raise(nameof(Coupon));
        SelectedCustomer = Customers[0];
        RaiseTotals();
        Reload();
        Raise(nameof(HoldText)); Raise(nameof(HeldButtonText));
    }

    void Hold()
    {
        try
        {
            var name = _customer.Id > 0 ? _customer.Name : string.Format(L["Pos_HeldName"], _s.Clock.Now.ToString("HH:mm", CultureInfo.InvariantCulture));
            _s.Held.Save(_shell.User!.Id, _customer.Id > 0 ? _customer.Id : null, name, Cart.Select(c => new HeldLine(c.Variant.ProductId, c.Qty, c.Price)).ToList());
            ClearCart();
            _shell.Toast(L["Toast_Held"], BadgeKind.Info);
            _shell.RefreshNotifications();
        }
        catch (PosException ex) { _shell.ToastError(ex); }
    }

    /// <summary>Resumes a held cart, revalidating stock and prices before payment (and saying what changed).</summary>
    public void ResumeHeld(long id)
    {
        if (Cart.Count > 0) { _shell.Toast(L["Toast_FinishCartFirst"], BadgeKind.Warning); return; }
        var held = _s.Held.List().FirstOrDefault(h => h.Id == id);
        var lines = _s.Held.Resume(id);
        var notes = 0;
        foreach (var l in lines)
        {
            var info = _s.Catalog.GetById(l.ProductId);
            if (info is null || !info.Active) { notes++; continue; }
            var style = _s.Styles.Get(info.StyleId)!;
            var row = _s.Styles.Variants(info.StyleId,_s.Stock.SalesLocation).First(v => v.ProductId == l.ProductId);
            var v = new VariantItem(row, style.Input.NameEn, style.Input.NameAlt, Enum.TryParse<GarmentKind>(style.Input.Kind, out var k) ? k : GarmentKind.TShirt);
            var qty = Math.Min(l.Quantity, v.Stock);
            if (qty <= 0) { notes++; continue; }
            if (qty < l.Quantity || (l.PriceAtHold > 0 && l.PriceAtHold != v.Price)) notes++;
            Cart.Add(new CartLine { Variant = v, Qty = qty, Edited = RaiseTotals });
        }

        if (held?.CustomerId is long cid) SelectedCustomer = Customers.FirstOrDefault(c => c.Id == cid) ?? Customers[0];
        RaiseTotals();
        Raise(nameof(HoldText)); Raise(nameof(HeldButtonText));
        _shell.Toast(notes > 0 ? L["Toast_ResumedChanged"] : L["Toast_Resumed"], notes > 0 ? BadgeKind.Warning : BadgeKind.Info);
    }

    void OpenDiscount() => _shell.ShowDialog(new DiscountViewModel(_shell, Subtotal, _discount, amount =>
    {
        if (amount <= 0) { _discount = 0; _discountApprover = null; RaiseTotals(); return; }
        if (_shell.IsOwner) { _discount = amount; _discountApprover = null; RaiseTotals(); _shell.Toast(L["Toast_DiscountApplied"]); return; }
        // Cashiers need an owner to approve exactly this discount.
        _shell.ShowDialog(new ApprovalViewModel(_shell, $"{L["Pos_Discount"]}: {L.Money(amount)}", owner =>
        {
            _discount = amount; _discountApprover = owner; RaiseTotals(); _shell.Toast(L["Toast_DiscountApplied"]);
        }));
    }));

    void Pay()
    {
        RefreshPromotionQuote(false);Raise(nameof(Total));
        if(HasPricingError){_shell.Toast(PricingError,BadgeKind.Danger);return;}
        if (!_shell.HasShift) { _shell.Toast(L["Toast_NeedShift"], BadgeKind.Warning); return; }
        if (_shell.Shift is { } sh && _shell.User is { } u && sh.CashierId != u.Id && !_shell.IsOwner) { _shell.Toast(L["Toast_ShiftOther"], BadgeKind.Warning); return; }
        _shell.ShowDialog(new PaymentViewModel(_shell, this));
    }

    public PostSaleRequest BuildRequest(Guid operationId, IReadOnlyList<PaymentRequest> payments, long? customerId,
        CreditArrangement? arrangement = null, long? limitApprover = null) => new(
        operationId, _shell.Shift!.Id, _shell.User!.Id, customerId,
        Cart.Select(c => new SaleLineRequest(c.Variant.ProductId, c.Qty)).ToList(),
        _discount > 0 ? LocalCashier.Domain.Discount.Fixed(_discount) : null, payments, _discountApprover ?? _promotionApprover ?? limitApprover, arrangement,
        Coupon:Coupon,SuppressedPromotionIds:_suppressedPromotions.ToArray(),PromotionOverrideReason:_promotionReason);

    public void SaleCompleted()
    {
        ReloadCustomers();
        ClearCart();
        _shell.Refresh();
    }
}

// ===================================================================== dialogs
public sealed class SizeOption(string size, decimal stock, bool exists) : ObservableObject
{
    bool _selected;
    public string Size { get; } = size;
    public decimal Stock { get; } = stock;
    public bool Exists { get; } = exists;
    public bool IsAvailable => Exists && Stock > 0;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class ColorPick(string hex, string nameEn, string nameAlt) : ObservableObject
{
    bool _selected;
    public string Hex { get; } = hex;
    public string NameEn { get; } = nameEn;
    public string NameAlt { get; } = nameAlt;
    public string Name => Localizer.Instance.Pick(NameEn, string.IsNullOrEmpty(NameAlt) ? NameEn : NameAlt);
    public string Contrast => Hex is "#F3F4F6" or "#D6B98C" or "#F1E4CF" or "#A9C7E8" ? "#0F172A" : "#FFFFFF";
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>Choose size and color for a style with several variants (scanning a barcode skips this).</summary>
public sealed class VariantPickerViewModel : DialogViewModel
{
    readonly IReadOnlyList<VariantItem> _variants;
    readonly Action<VariantItem> _add;
    ColorPick? _color;
    SizeOption? _size;

    public VariantPickerViewModel(ShellViewModel shell, StyleItem style, IReadOnlyList<VariantItem> variants, Action<VariantItem> add) : base(shell)
    {
        Style = style;
        _variants = variants;
        _add = add;
        var cfg = shell.S.Config.Get();
        Option1Name = cfg.HasOption1 ? cfg.Option1Name! : Localizer.Instance["Col_Size"];
        Option2Name = cfg.HasOption2 ? cfg.Option2Name! : Localizer.Instance["Col_Color"];
        Colors = variants.GroupBy(v => v.ColorNameEn).Select(g => new ColorPick(g.First().ColorHex, g.Key, g.First().ColorNameAlt)).ToList();
        HasColors = Colors.Any(c => c.NameEn.Length > 0);
        SwatchColors = HasColors && cfg.Option2IsColor && Colors.Any(c => c.Hex.Length > 0);
        HasSizes = variants.Any(v => v.Size.Length > 0);
        AllSizes = variants.Select(v => v.Size).Distinct().ToList();
        Sizes = [];
        PickColorCommand = new RelayCommand(p => SelectColor((ColorPick)p!));
        PickSizeCommand = new RelayCommand(p => SelectSize((SizeOption)p!));
        AddCommand = new RelayCommand(Add, () => Selected is { Stock: > 0 });
        var first = Colors.FirstOrDefault(c => variants.Any(v => v.ColorNameEn == c.NameEn && v.Stock > 0)) ?? Colors[0];
        SelectColor(first);
    }

    public override double Width => 460;
    public StyleItem Style { get; }
    public string Option1Name { get; }
    public string Option2Name { get; }
    public bool HasColors { get; }
    public bool SwatchColors { get; }
    public bool TextColors => HasColors && !SwatchColors;
    public bool HasSizes { get; }
    public IReadOnlyList<ColorPick> Colors { get; }
    public IReadOnlyList<string> AllSizes { get; }
    public ObservableCollection<SizeOption> Sizes { get; }
    public ICommand PickColorCommand { get; }
    public ICommand PickSizeCommand { get; }
    public ICommand AddCommand { get; }
    public VariantItem? Selected => _color is null || _size is null ? null : _variants.FirstOrDefault(v => v.ColorNameEn == _color.NameEn && v.Size == _size.Size);
    public string SummaryText => Selected is { } v ? $"{v.Label} — {Localizer.Instance.Money(v.Price)} · {string.Format(Localizer.Instance["Pick_InStock"], v.Stock)}" : Localizer.Instance["Pick_Choose"];

    void SelectColor(ColorPick color)
    {
        foreach (var c in Colors) c.IsSelected = c == color;
        _color = color;
        Sizes.Clear();
        foreach (var size in AllSizes)
        {
            var v = _variants.FirstOrDefault(x => x.ColorNameEn == color.NameEn && x.Size == size);
            Sizes.Add(new SizeOption(size, v?.Stock ?? 0, v is not null));
        }

        _size = Sizes.FirstOrDefault(s => s.IsAvailable);
        if (_size is not null) _size.IsSelected = true;
        Raise(nameof(SummaryText)); CommandManager.InvalidateRequerySuggested();
    }

    void SelectSize(SizeOption size)
    {
        if (!size.IsAvailable) return;
        foreach (var s in Sizes) s.IsSelected = s == size;
        _size = size;
        Raise(nameof(SummaryText)); CommandManager.InvalidateRequerySuggested();
    }

    void Add()
    {
        if (Selected is not { } v) return;
        Shell.CloseDialog();
        _add(v);
    }
}

public sealed class HeldCartsViewModel : DialogViewModel
{
    readonly PosViewModel _pos;

    public HeldCartsViewModel(ShellViewModel shell, PosViewModel pos) : base(shell)
    {
        _pos = pos;
        Carts = new ObservableCollection<HeldCartItem>(Load());
        ResumeCommand = new RelayCommand(p => { Shell.CloseDialog(); _pos.ResumeHeld(((HeldCartItem)p!).Id); });
        DiscardCommand = new RelayCommand(p =>
        {
            var item = (HeldCartItem)p!;
            Shell.S.Held.Discard(item.Id);
            Carts.Remove(item);
            Raise(nameof(IsEmpty));
            Shell.RefreshNotifications();
        });
    }

    public override double Width => 520;
    public ObservableCollection<HeldCartItem> Carts { get; }
    public bool IsEmpty => Carts.Count == 0;
    public ICommand ResumeCommand { get; }
    public ICommand DiscardCommand { get; }

    IEnumerable<HeldCartItem> Load() => Shell.S.Held.List().Select(h => new HeldCartItem(h));
}

public sealed class HeldCartItem(HeldCartRow r)
{
    public long Id { get; } = r.Id;
    public string Name { get; } = r.Name;
    public int Items { get; } = r.ItemCount;
    public string Time { get; } = DateTimeOffset.Parse(r.CreatedAt, CultureInfo.InvariantCulture).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
}

public sealed class DiscountViewModel(ShellViewModel shell, decimal subtotal, decimal current, Action<decimal> apply) : DialogViewModel(shell)
{
    string _custom = current > 0 ? current.ToString("0.##", CultureInfo.InvariantCulture) : "";
    int _percent;

    public override double Width => 400;
    public decimal Subtotal { get; } = subtotal;
    public int[] Presets { get; } = [5, 10, 15, 20];
    public int Percent { get => _percent; private set { Set(ref _percent, value); Raise(nameof(Amount)); } }

    public string Custom
    {
        get => _custom;
        set { if (Set(ref _custom, value)) { _percent = 0; Raise(nameof(Percent)); Raise(nameof(Amount)); Validate(); } }
    }

    public decimal Amount => _percent > 0 ? Money.Round(Subtotal * _percent / 100m) : decimal.TryParse(_custom, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public ICommand PresetCommand => new RelayCommand(p => { _custom = ""; Raise(nameof(Custom)); Percent = (int)p!; Validate(); });
    public ICommand ApplyCommand => new RelayCommand(() => { if (!Validate()) return; Shell.CloseDialog(); apply(Amount); });
    public ICommand RemoveCommand => new RelayCommand(() => { Shell.CloseDialog(); apply(0); });

    bool Validate()
    {
        var l = Localizer.Instance;
        var m = Amount > Subtotal ? l["Err_DiscountTooHigh"] : Amount < 0 ? l["Err_Number"] : null;
        SetError(nameof(Custom), m);
        return m is null;
    }
}

public sealed class PaymentMethodOption(string key, string glyph) : LocalizedObject
{
    readonly PaymentMethodRow? _row;
    public PaymentMethodOption(PaymentMethodRow row,bool payPrefix=false):this((payPrefix?"Pay_":"")+(row.Code switch {"cash"=>"Cash","card"=>"Card","transfer"=>"Transfer","credit"=>"Credit",_=>row.Code}),
        row.Kind switch {"cash"=>Icons.Money,"card"=>Icons.Card,"credit"=>Icons.Credit,_=>Icons.Transfer}) { _row=row; }
    public string Key { get; } = key;
    public string Glyph { get; } = glyph;
    public long Id=>_row?.Id??Key switch {"Cash"=>1,"Card"=>2,"Transfer"=>3,"Credit"=>4,_=>0};
    public string Kind=>_row?.Kind??Key.ToLowerInvariant();
    public string Code=>_row?.Code??Key.ToLowerInvariant();
    public PaymentMethod MethodKind=>Kind switch {"cash"=>PaymentMethod.Cash,"card"=>PaymentMethod.Card,"transfer"=>PaymentMethod.Transfer,_=>throw new PosException(PosError.PaymentMismatch,"Customer credit is the unpaid remainder.")};
    public string Label => _row is null?Localizer.Instance["Pay_" + Key]:Localizer.Instance.Pick(_row.NameEn,_row.NameAr);
    public override string ToString()=>Label;
}

/// <summary>
/// Payment for the current cart. The operation id is fixed for the life of the dialog, so retrying after an error (or a double click)
/// can never post a second sale.
/// </summary>
public sealed partial class PaymentViewModel : DialogViewModel
{
    readonly PosViewModel _pos;
    readonly Guid _operationId = Guid.NewGuid();
    string _method = "Cash";
    string _cash;
    string _electronic;
    string _notes = "";
    string _error = "";
    bool _posting;
    string? _expiryReason;
    long? _expiryApprover;
    long? _lastLimitApprover;
    bool _expiryBlocked;

    public PaymentViewModel(ShellViewModel shell, PosViewModel pos) : base(shell)
    {
        _pos = pos;
        _cash = Total.ToString("0.##", CultureInfo.InvariantCulture);
        _electronic = Total.ToString("0.##", CultureInfo.InvariantCulture);
        Methods = shell.S.PaymentMethods.List().Select(m=>new PaymentMethodOption(m)).ToList();
        TenderMethods=Methods.Where(m=>m.Kind!="credit").ToList();
        _method=Methods.FirstOrDefault()?.Key??"";
        if(_method=="Credit")_cash="0";
        PaymentLines.CollectionChanged+=(_,_)=>Recalculate();
        AddPaymentCommand=new RelayCommand(()=>{if(TenderMethods.Count>0)PaymentLines.Add(new(TenderMethods,Recalculate){Amount=Math.Max(0,RemainingOnAccount).ToString("0.##",CultureInfo.InvariantCulture)});},()=>TenderMethods.Count>0);
        RemovePaymentCommand=new RelayCommand(p=>PaymentLines.Remove((PaymentTenderEdit)p!));
        Customers = pos.Customers;
        SelectedCustomer = pos.SelectedCustomer;
        CompleteCommand = new RelayCommand(Complete, () => IsValid && !_posting);
        SelectMethodCommand = new RelayCommand(p => Method = (string)p!);
        ApproveExpiryCommand=new RelayCommand(()=>
        {
            Shell.ShowDialog(new ReasonViewModel(Shell,Localizer.Instance["Stock_ExpiryOverride"],Localizer.Instance["Stock_ExpiryBlocked"],reason=>
            {
                void Approved(long owner) { _expiryApprover=owner;_expiryReason=reason;Shell.ShowDialog(this);Submit(_lastLimitApprover); }
                if(Shell.IsOwner)Approved(Shell.User!.Id);
                else Shell.ShowDialog(new ApprovalViewModel(Shell,Localizer.Instance["Stock_ExpiryOverride"],Approved));
            }));
        });
        Recalculate();
    }

    public override double Width => IsSplit?720:560;
    public decimal Total => Math.Max(0,_pos.Total-RedeemedValue);
    public CreditPlanState Credit { get; } = new();
    public IReadOnlyList<PaymentMethodOption> Methods { get; }
    public IReadOnlyList<CustomerItem> Customers { get; private set; }
    CustomerItem _customer = null!;
    public CustomerItem SelectedCustomer { get => _customer; set { if (Set(ref _customer, value ?? Customers[0])) { _redeemPoints="0";Raise(nameof(RedeemPointsText));_pos.SelectedCustomer=_customer;Recalculate(); } } }
    public ICommand CompleteCommand { get; }
    public ICommand SelectMethodCommand { get; }
    public ICommand ApproveExpiryCommand { get; }
    public bool ExpiryBlocked {get=>_expiryBlocked;private set=>Set(ref _expiryBlocked,value);}

    public string Method
    {
        get => _method;
        set
        {
            if (!Set(ref _method, value)) return;
            _cardConfirmed=false;Raise(nameof(ConfirmedOnTerminal));
            Raise(nameof(IsCash)); Raise(nameof(IsElectronic)); Raise(nameof(IsCredit)); Raise(nameof(ShowCashField)); Raise(nameof(ShowCustomer));
            if (value == "Credit") CashText = "0";
            else if (value == "Cash") CashText = Total.ToString("0.##", CultureInfo.InvariantCulture);
            Recalculate();
        }
    }

    public bool IsCash => IsSimple && SelectedMethod?.Kind=="cash";
    public bool IsElectronic => IsSimple && SelectedMethod?.Kind is "card" or "transfer";
    public bool IsCredit => IsSimple && SelectedMethod?.Kind=="credit";
    public bool ShowCashField => IsSimple && CashEnabled && (IsCash || IsCredit || IsElectronic && ElectronicValue < Total);
    public bool ShowCustomer => IsCredit || RemainingOnAccount > 0;

    // Optional customer details captured at checkout (walk-in sales only): the customer is saved to Customers and the name goes on the receipt.
    string _custName = "", _custPhone = "";
    long? _capturedId;
    public bool ShowCapture => !ShowCustomer && SelectedCustomer.Id == 0;
    public string CustomerName { get => _custName; set { if (Set(ref _custName, value)) { _capturedId = null; Recalculate(); } } }
    public string CustomerPhone { get => _custPhone; set { if (Set(ref _custPhone, value)) { _capturedId = null; Raise(nameof(MatchedCustomer)); Raise(nameof(HasMatch)); Recalculate(); } } }
    CustomerItem? Match => CustomerService.NormalizePhone(_custPhone) is { } p ? Customers.FirstOrDefault(c => c.Id > 0 && c.Phone == p) : null;
    public string MatchedCustomer => Match?.Name ?? "";
    public bool HasMatch => Match is not null;

    public string CashText { get => _cash; set { if (Set(ref _cash, value)) Recalculate(); } }
    public string ElectronicText { get => _electronic; set { if (Set(ref _electronic, value)) { _cardConfirmed=false;Raise(nameof(ConfirmedOnTerminal));Raise(nameof(ShowCashField)); Recalculate(); } } }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public string ErrorMessage { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    static decimal Parse(string s) => decimal.TryParse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : -1;

    decimal CashValue => Parse(_cash);
    decimal ElectronicValue => IsElectronic ? Parse(_electronic) : 0m;
    decimal NeededFromCash => Math.Max(0, Total - (IsElectronic ? Math.Max(0, ElectronicValue) : 0m));
    public decimal Change => IsSplit?Math.Max(0,SplitCash-Math.Max(0,Total-SplitElectronic)):ShowCashField && CashValue > NeededFromCash ? CashValue - NeededFromCash : 0m;
    public decimal RemainingOnAccount => IsSplit?Math.Max(0,Total-SplitElectronic-SplitCash):Math.Max(0, NeededFromCash - (ShowCashField?Math.Max(0,CashValue):0));
    public bool IsValid => !HasErrors && !_posting && (RemainingOnAccount <= 0 || Credit.IsValid);

    void Recalculate()
    {
        var l = Localizer.Instance;
        ValidateLoyalty();
        string? cashErr = null, elecErr = null;
        if (IsElectronic && (ElectronicValue <= 0 || ElectronicValue > Total || Money.Round(ElectronicValue)!=ElectronicValue)) elecErr = l["Err_ElectronicAmount"];
        if (ShowCashField && (CashValue < 0 || Money.Round(CashValue)!=CashValue)) cashErr = l["Err_Number"];
        else if (RemainingOnAccount > 0 && cashErr is null)
        {
            if(!Methods.Any(m=>m.Kind=="credit"))cashErr=l["Pay_CreditDisabled"];
            else if (SelectedCustomer.Id == 0) cashErr = IsCredit ? l["Err_PickCustomer"] : l["Err_ReceivedLess"];
            else if (!SelectedCustomer.CreditEnabled) cashErr = l["Err_NoCredit"];
            // Over the limit is not an error: an owner may approve it (the owner cashier directly, a cashier by owner sign-in at confirm).
        }

        SetError(nameof(CashText), cashErr);
        SetError(nameof(ElectronicText), elecErr);
        var capturing = ShowCapture;
        var phone = CustomerService.NormalizePhone(_custPhone);
        SetError(nameof(CustomerPhone), capturing && phone is not null && !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^01\d{9}$") ? l["Err_Phone"] : null);
        SetError(nameof(CustomerName), capturing && phone is not null && Match is null && string.IsNullOrWhiteSpace(_custName) ? l["Err_Name"] : null);
        Raise(nameof(ShowCapture));
        SetError("Tenders",Methods.Count==0?l["Pay_NoMethods"]:IsSplit && (PaymentLines.Any(p=>!p.IsValid)||SplitElectronic>Total)?l["Pay_BadSplit"]:
            IsCard && !SingleCardValid?l["Pay_ConfirmCard"]:null);
        Credit.Update(Shell, SelectedCustomer, RemainingOnAccount);
        ErrorMessage = "";
        Raise(nameof(Change)); Raise(nameof(RemainingOnAccount)); Raise(nameof(ShowCustomer)); Raise(nameof(IsValid)); Raise(nameof(ShowCashField));
        Raise(nameof(IsCard));
        CommandManager.InvalidateRequerySuggested();
    }

    void Complete()
    {
        if (RemainingOnAccount > 0 && Credit.OverLimit && !Shell.IsOwner)
        {
            Shell.ShowDialog(new ApprovalViewModel(Shell, Localizer.Instance["Credit_OverLimitApprove"], owner => { Shell.ShowDialog(this); Submit(owner); }));
            return;
        }
        Submit(null);
    }

    void Submit(long? limitApprover)
    {
        _lastLimitApprover=limitApprover;
        _posting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            long? customer = SelectedCustomer.Id > 0 ? SelectedCustomer.Id : null;
            if (customer is null && ShowCapture && (!string.IsNullOrWhiteSpace(_custName) || !string.IsNullOrWhiteSpace(_custPhone)))
            {
                customer = _capturedId ??= Shell.S.Customers.FindOrAdd(Shell.User!.Id, _custName, _custPhone);
                var previousTotal = Total;
                var cashWasTotal = CashValue == previousTotal;
                var electronicWasTotal = ElectronicValue == previousTotal;
                _pos.ReloadCustomers();
                Customers = _pos.Customers;
                Raise(nameof(Customers));
                SelectedCustomer = Customers.Single(c => c.Id == customer);
                // A phone match may unlock group offers. Display the customer's quote
                // before posting; a changed amount needs review and fresh card confirmation.
                if (Total != previousTotal)
                {
                    if (cashWasTotal) CashText = Total.ToString("0.##", CultureInfo.InvariantCulture);
                    if (electronicWasTotal) ElectronicText = Total.ToString("0.##", CultureInfo.InvariantCulture);
                    RefreshLoyaltyQuote();
                    ErrorMessage = Localizer.Instance["Pay_CustomerPriceChanged"];
                    return;
                }
            }
            var payments = IsSplit?PaymentLines.Select(p=>p.ToRequest()).ToList():SimplePayments();
            var arrangement = RemainingOnAccount > 0 ? Credit.ToArrangement() : null;
            var request=_pos.BuildRequest(_operationId,payments,customer,arrangement,limitApprover);
            request=request with {RedeemPoints=RedeemPoints,ExpectedTotal=_pos.Total,ExpectedRedemptionValue=RedeemedValue};
            if(_expiryReason is not null)request=request with {ExpiredStockReason=_expiryReason,ApproverId=_expiryApprover};
            var result = Shell.S.Sales.Post(request);
            Shell.CloseDialog();
            _pos.SaleCompleted();
            Shell.ShowDialog(new SaleCompleteViewModel(Shell, result));
        }
        catch (PosException ex)
        {
            ExpiryBlocked=ex.Code==PosError.ExpiredStock;
            if(ex.Code==PosError.PaymentMismatch){_pos.RefreshCurrentQuote();RefreshLoyaltyQuote();}
            ErrorMessage = Shell.ErrorText(ex);
        }
        finally
        {
            _posting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>Shown after a successful sale: preview, print status, reprint and PDF. Printing problems never undo the sale.</summary>
public sealed class SaleCompleteViewModel : DialogViewModel
{
    readonly PostSaleResult _result;
    string _status = "";
    BadgeKind _statusKind = BadgeKind.Info;

    public SaleCompleteViewModel(ShellViewModel shell, PostSaleResult result) : base(shell)
    {
        _result = result;
        Preview = BuildPreview(false);
        PrintCommand = new RelayCommand(() => PrintReceipt(copy: false));
        PrintA4Command = new RelayCommand(PrintA4);
        SavePdfCommand = new RelayCommand(SavePdf);
        NewSaleCommand = new RelayCommand(Shell.CloseDialog);
        if (Shell.S.Settings.GetValue("auto_print_receipt") == "1") PrintReceipt(copy: false);
        else Status = Localizer.Instance["Print_Ready"];
    }

    public override double Width => 440;
    public string Number => _result.Number;
    public decimal Total => _result.Total;
    public decimal Change => _result.Change;
    public bool HasChange => _result.Change > 0;
    public decimal Debt => _result.Debt;
    public bool HasDebt => _result.Debt > 0;
    public FrameworkElement Preview { get; }
    public ICommand PrintCommand { get; }
    public ICommand PrintA4Command { get; }
    public ICommand SavePdfCommand { get; }
    public ICommand NewSaleCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public BadgeKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    FrameworkElement BuildPreview(bool copy) =>
        DocumentBuilder.Receipt80(Shell.S.Sales.GetReceipt(_result.SaleId), Shell.S.Printing.Store(), copy);

    void PrintReceipt(bool copy)
    {
        var l = Localizer.Instance;
        var job = Shell.S.PrintJobs.ForSale(_result.SaleId).FirstOrDefault(j => j.Kind == "receipt80" && j.Status != PrintStatus.Submitted);
        var status = copy || job is null ? Shell.S.Printing.Reprint(_result.SaleId) : Shell.S.Printing.Print(job.Id);
        Report(status);
    }

    void PrintA4()
    {
        var id = Shell.S.PrintJobs.Enqueue(_result.SaleId, "a4", isCopy: false);
        Report(Shell.S.Printing.Print(id));
    }

    void Report(PrintStatus status)
    {
        var l = Localizer.Instance;
        var last = Shell.S.PrintJobs.ForSale(_result.SaleId).LastOrDefault();
        Status = status == PrintStatus.Submitted ? l["Print_Sent"] : string.Format(l["Print_Failed"], last?.Error ?? "");
        StatusKind = status == PrintStatus.Submitted ? BadgeKind.Success : BadgeKind.Danger;
        Shell.RefreshNotifications();
    }

    void SavePdf()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PDF|*.pdf", FileName = $"{_result.Number}.pdf" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Shell.S.Printing.SavePdf(_result.SaleId, dlg.FileName);
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
