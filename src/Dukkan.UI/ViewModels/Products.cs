using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace Dukkan.UI;

// ===================================================================== product list
public sealed class ProductsViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    string _search = "";
    Option _category;
    Option _status;
    List<StyleItem> _all = [];

    public ProductsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Categories = new[] { new Option("Cat_AllCategories") }.Concat(shell.S.Config.Categories().Select(c => new Option(c.Name, () => CategoryNames.Label(c.Name)))).ToList();
        Statuses = new[] {"Status_All", "Status_Active", "Status_LowStock", "Status_OutOfStock"}.Select(k => new Option(k)).ToList();
        _category = Categories[0];
        _status = Statuses[0];
        Pager = new Pager<StyleItem>(7);
        AddCommand = new RelayCommand(() => _shell.OpenProductEditor(null));
        ImportCommand = new RelayCommand(() => _shell.ShowDialog(new ImportViewModel(_shell, Load)));
        EditCommand = new RelayCommand(p => _shell.OpenProductEditor(((StyleItem)p!).Id));
        ArchiveCommand = new RelayCommand(p => Archive((StyleItem)p!));
        DuplicateCommand = new RelayCommand(p => Duplicate((StyleItem)p!));
        LabelsCommand = new RelayCommand(p => OpenLabels(p is StyleItem s ? [s] : SelectedStyles()));
        ClearFiltersCommand = new RelayCommand(() => { Search = ""; Category = Categories[0]; Status = Statuses[0]; });
        ArchiveSelectedCommand = new RelayCommand(ArchiveSelected);
        ClearSelectionCommand = new RelayCommand(() => SetAll(false));
        Load();
    }

    public Localizer L => Localizer.Instance;
    public bool IsOwner => _shell.IsOwner;
    public IReadOnlyList<Option> Categories { get; }
    public IReadOnlyList<Option> Statuses { get; }
    public Pager<StyleItem> Pager { get; }
    public ICommand AddCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand LabelsCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand ArchiveSelectedCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public string Search { get => _search; set { if (Set(ref _search, value)) Apply(); } }
    public Option Category { get => _category; set { if (Set(ref _category, value)) Apply(); } }
    public Option Status { get => _status; set { if (Set(ref _status, value)) Apply(); } }
    public int SelectedCount => _all.Count(p => p.IsChecked);
    public bool HasSelection => SelectedCount > 0;

    public bool? AllChecked
    {
        get => Pager.Items.Count > 0 && Pager.Items.All(p => p.IsChecked) ? true : Pager.Items.Any(p => p.IsChecked) ? null : false;
        set => SetAll(value == true);
    }

    IReadOnlyList<StyleItem> SelectedStyles() => _all.Where(p => p.IsChecked).ToList();

    void Load()
    {
        _all = _shell.S.Styles.List().Select(r => new StyleItem(r)).ToList();
        foreach (var p in _all)
            p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StyleItem.IsChecked)) { Raise(nameof(SelectedCount)); Raise(nameof(HasSelection)); Raise(nameof(AllChecked)); } };
        Apply();
    }

    void SetAll(bool on)
    {
        foreach (var p in Pager.Items) p.IsChecked = on;
        Raise(nameof(AllChecked));
    }

    void Apply()
    {
        var q = _all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var ids = _shell.S.Styles.List(_search.Trim()).Select(r => r.Id).ToHashSet();   // matches name, SKU or barcode
            q = q.Where(p => ids.Contains(p.Id));
        }

        if (_category.Key != "Cat_AllCategories") q = q.Where(p => p.CategoryKey == _category.Key);
        if (_status.Key != "Status_All") q = q.Where(p => "Status_" + p.State == _status.Key);
        Pager.SetSource(q.ToList());
        Raise(nameof(AllChecked));
    }

    void Archive(StyleItem p) => _shell.Confirm(L["Confirm_ArchiveTitle"], string.Format(L["Confirm_ArchiveProduct"], p.Name), L["Action_Archive"], true, () =>
    {
        _shell.S.Styles.Archive(_shell.User!.Id, p.Id);
        Load();
        _shell.Toast(L["Toast_Archived"], BadgeKind.Info);
    });

    void ArchiveSelected() => _shell.Confirm(L["Confirm_ArchiveTitle"], string.Format(L["Confirm_ArchiveSelected"], SelectedCount), L["Action_Archive"], true, () =>
    {
        foreach (var s in SelectedStyles()) _shell.S.Styles.Archive(_shell.User!.Id, s.Id);
        Load();
        Raise(nameof(SelectedCount)); Raise(nameof(HasSelection));
        _shell.Toast(L["Toast_Archived"], BadgeKind.Info);
    });

    /// <summary>Copies a style and its variants (new SKUs and barcodes, no stock).</summary>
    void Duplicate(StyleItem p)
    {
        try
        {
            var s = _shell.S;
            var detail = s.Styles.Get(p.Id)!;
            var variants = detail.Variants.Select(v => new VariantInput(v.Size ?? "", v.ColorNameEn ?? "", v.ColorNameAlt ?? "", v.ColorHex ?? "#9CA3AF")).ToList();
            s.Styles.CreateStyle(_shell.User!.Id, detail.Input with { NameEn = detail.Input.NameEn + " (copy)", NameAlt = detail.Input.NameAlt is null ? null : detail.Input.NameAlt + " (نسخة)" }, variants);
            Load();
            _shell.Toast(L["Toast_Duplicated"], BadgeKind.Info);
        }
        catch (PosException ex) { _shell.ToastError(ex); }
    }

    void OpenLabels(IReadOnlyList<StyleItem> styles)
    {
        if (styles.Count == 0) return;
        _shell.ShowDialog(new LabelPrintViewModel(_shell, styles.Select(s => s.Id).ToList()));
    }
}

// ===================================================================== barcode labels
public sealed class LabelRow(VariantRow v, string styleName, string styleNameAlt) : ObservableObject
{
    string _copies = "1";

    public VariantRow Variant { get; } = v;
    public string StyleName { get; } = styleName;
    public string StyleNameAlt { get; } = styleNameAlt;
    public string Name => string.IsNullOrWhiteSpace(StyleNameAlt) ? StyleName : Localizer.Instance.Pick(StyleName, StyleNameAlt);
    public string Label => string.Join(" / ", new[] { Variant.Size, Localizer.Instance.Pick(Variant.ColorNameEn ?? "", Variant.ColorNameAlt ?? Variant.ColorNameEn ?? "") }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string Copies { get => _copies; set => Set(ref _copies, value); }
    public int CopiesValue => int.TryParse(_copies, out var n) && n is >= 0 and <= 500 ? n : -1;
}

public sealed class LabelPrintViewModel : DialogViewModel
{
    bool _sheet;
    string _skip = "0";
    string _message = "";

    public LabelPrintViewModel(ShellViewModel shell, IReadOnlyList<long> styleIds) : base(shell)
    {
        Rows = [];
        foreach (var id in styleIds)
        {
            var d = shell.S.Styles.Get(id);
            if (d is null) continue;
            foreach (var v in d.Variants.Where(v => v.Active))
                Rows.Add(new LabelRow(v, d.Input.NameEn, d.Input.NameAlt ?? ""));
        }

        PrintCommand = new RelayCommand(Print);
        SetCopiesCommand = new RelayCommand(p => { foreach (var r in Rows) r.Copies = p is "stock" ? Math.Max(0, r.Variant.Stock).ToString(CultureInfo.InvariantCulture) : "1"; });
    }

    public override double Width => 600;
    public ObservableCollection<LabelRow> Rows { get; }
    public ICommand PrintCommand { get; }
    public ICommand SetCopiesCommand { get; }
    public bool SheetMode { get => _sheet; set { if (Set(ref _sheet, value)) Raise(nameof(RollMode)); } }
    public bool RollMode { get => !_sheet; set => SheetMode = !value; }
    public string Skip { get => _skip; set => Set(ref _skip, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }

    void Print()
    {
        var l = Localizer.Instance;
        if (Rows.Any(r => r.CopiesValue < 0)) { Message = l["Err_Copies"]; return; }
        var labels = Rows.SelectMany(r => Enumerable.Repeat(
            new LabelItem(r.StyleName, r.Variant.Size, r.Variant.ColorNameEn, r.Variant.Price, r.Variant.Barcode ?? "", r.Variant.Sku, "EGP"), r.CopiesValue)).ToList();
        if (labels.Count == 0) { Message = l["Err_NoLabels"]; return; }
        var error = Shell.S.Printing.PrintLabels(labels, _sheet, skip: int.TryParse(_skip, out var s) ? Math.Max(0, s) : 0);
        if (error is not null) { Message = string.Format(l["Print_Failed"], error); return; }
        Shell.CloseDialog();
        Shell.Toast(string.Format(l["Toast_LabelsSent"], labels.Count), BadgeKind.Success);
    }
}

// ===================================================================== add / edit product (style + variants)
public sealed class SizeChoice(string size, bool selected, bool locked) : ObservableObject
{
    bool _selected = selected;
    public string Size { get; } = size;
    public bool Locked { get; } = locked;
    public bool IsSelected { get => _selected; set { if (!Locked && Set(ref _selected, value)) Changed?.Invoke(); } }
    public event Action? Changed;
}

public sealed class ColorChoice(ColorOption option, bool selected, bool locked) : ObservableObject
{
    bool _selected = selected;
    public ColorOption Option { get; } = option;
    public bool Locked { get; } = locked;
    public bool IsSelected { get => _selected; set { if (!Locked && Set(ref _selected, value)) Changed?.Invoke(); } }
    public string Hex => Option.Hex;
    public string Contrast => Option.Hex is "#F3F4F6" or "#D6B98C" or "#F1E4CF" or "#A9C7E8" ? "#0F172A" : "#FFFFFF";
    public event Action? Changed;
}

public sealed class VariantEdit : ObservableObject
{
    string _stock;
    readonly Action? _stockChanged;

    public VariantEdit(string size, string colorHex, string colorEn, string colorAlt, string sku, string? barcode, decimal stock, long? productId, Action? stockChanged = null)
    {
        Size = size; ColorHex = colorHex; ColorNameEn = colorEn; ColorNameAlt = colorAlt; Sku = sku; Barcode = barcode; OriginalStock = stock; ProductId = productId;
        _stock = stock.ToString("0.###", CultureInfo.InvariantCulture);
        _stockChanged = stockChanged;
    }

    public long? ProductId { get; }
    public string Size { get; }
    public string ColorHex { get; }
    public string ColorNameEn { get; }
    public string ColorNameAlt { get; }
    public string Sku { get; }
    public string? Barcode { get; }
    public decimal OriginalStock { get; }
    public bool IsNew => ProductId is null;
    public string ColorName => Localizer.Instance.Pick(ColorNameEn, string.IsNullOrEmpty(ColorNameAlt) ? ColorNameEn : ColorNameAlt);
    public bool HasColor => ColorHex.Length > 0;
    public string Stock { get => _stock; set { if (Set(ref _stock, value)) _stockChanged?.Invoke(); } }
    public decimal StockValue => decimal.TryParse(_stock, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n >= 0 && n == decimal.Round(n, 3) ? n : -1;
}

public sealed class ProductEditViewModel : ValidatableViewModel
{
    readonly ShellViewModel _shell;
    readonly long? _styleId;
    string _name = "", _nameAlt = "", _sku = "", _brand = "", _description = "", _basePrice = "", _costPrice = "", _lowStock = "10", _tax;
    Option _category;
    Option _status;
    Option _kind;
    bool _track = true, _allowDiscount = true;
    int _tab;
    VariantEdit? _preview;

    public ProductEditViewModel(ShellViewModel shell, long? styleId)
    {
        _shell = shell;
        _styleId = styleId;
        IsNew = styleId is null;
        var catNames = shell.S.Config.Categories().Select(c => c.Name).ToList();
        if (styleId is long cid && shell.S.Styles.Get(cid) is { } cd && cd.Input.CategoryKey.Length > 0 && !catNames.Contains(cd.Input.CategoryKey)) catNames.Add(cd.Input.CategoryKey);
        // Categories are optional: a new shop may not have created any yet.
        // Keep a selectable empty key without creating a synthetic database category.
        catNames.Add("");
        Categories = catNames.Select(c => new Option(c, () => CategoryNames.Label(c))).ToList();
        Statuses = new[] {"Status_Active", "Status_Inactive"}.Select(k => new Option(k)).ToList();
        Kinds = Enum.GetNames<GarmentKind>().Select(k => new Option("Kind_" + k)).ToList();
        Units = new[] { "pc", "kg", "g", "m", "l", "box", "pack" }.Select(x => new Option("Unit_" + x)).ToList();
        _unit = Units[0];
        _category = Categories[0];
        _status = Statuses[0];
        _kind = Kinds[0];
        _tax = shell.S.Settings.GetValue("default_tax_percent") ?? "0";
        var locked = new HashSet<string>();
        var lockedColors = new HashSet<string>();
        Variants = [];
        if (styleId is long id && shell.S.Styles.Get(id) is { } d)
        {
            var i = d.Input;
            _name = i.NameEn; _nameAlt = i.NameAlt ?? ""; _brand = i.Brand ?? ""; _description = i.Description ?? "";
            _basePrice = i.BasePrice.ToString("0.##", CultureInfo.InvariantCulture); _costPrice = i.Cost.ToString("0.##", CultureInfo.InvariantCulture);
            _lowStock = i.LowStockAlert.ToString(CultureInfo.InvariantCulture); _tax = (i.TaxRate * 100m).ToString("0.##", CultureInfo.InvariantCulture);
            _track = i.TrackInventory; _allowDiscount = i.AllowDiscount;
            _category = Categories.FirstOrDefault(c => c.Key == i.CategoryKey) ?? Categories[0];
            _status = Statuses[i.Active ? 0 : 1];
            _kind = Kinds.FirstOrDefault(k => k.Key == "Kind_" + i.Kind) ?? Kinds[0];
            _unit = Units.FirstOrDefault(x => x.Key == "Unit_" + i.Unit) ?? Units[0];
            _sku = d.Variants.FirstOrDefault()?.Sku.Split('-')[0] ?? "";
            foreach (var v in d.Variants)
            {
                Variants.Add(new VariantEdit(v.Size ?? "", v.ColorHex ?? "", v.ColorNameEn ?? "", v.ColorNameAlt ?? "", v.Sku, v.Barcode, v.Stock, v.ProductId, StockChanged));
                locked.Add(v.Size ?? ""); lockedColors.Add(v.ColorNameEn ?? "");
            }
        }

        Cfg = shell.S.Config.Get();
        var existing = Variants.ToList();
        HasOption1 = Cfg.HasOption1 || existing.Any(v => v.Size.Length > 0);
        HasOption2 = Cfg.HasOption2 || existing.Any(v => v.ColorNameEn.Length > 0);
        Option2IsColor = Cfg.Option2IsColor || (!Cfg.HasOption2 && existing.Any(v => v.ColorHex.Length > 0));
        var sizeValues = Cfg.Option1Presets.Concat(existing.Select(v => v.Size)).Where(s => s.Length > 0).Distinct().ToList();
        Sizes = new ObservableCollection<SizeChoice>(sizeValues.Select(s => new SizeChoice(s, locked.Contains(s), locked.Contains(s))));
        var colorOptions = (Option2IsColor ? GarmentOptions.Palette : Cfg.Option2Presets.Select(n => new ColorOption("", n, n)).ToArray()).ToList();
        foreach (var v in existing.Where(v => v.ColorNameEn.Length > 0 && !colorOptions.Any(o => o.NameEn == v.ColorNameEn)))
            colorOptions.Add(new ColorOption(v.ColorHex, v.ColorNameEn, string.IsNullOrEmpty(v.ColorNameAlt) ? v.ColorNameEn : v.ColorNameAlt));
        Colors = new ObservableCollection<ColorChoice>(colorOptions.Select(c => new ColorChoice(c, lockedColors.Contains(c.NameEn), lockedColors.Contains(c.NameEn))));
        AddSizeCommand = new RelayCommand(() =>
        {
            var v = _newSize.Trim();
            if (v.Length == 0 || Sizes.Any(s => string.Equals(s.Size, v, StringComparison.OrdinalIgnoreCase))) { NewSize = ""; return; }
            var choice = new SizeChoice(v, false, false); choice.Changed += RebuildVariants; Sizes.Add(choice); choice.IsSelected = true; NewSize = "";
        });
        AddColorCommand = new RelayCommand(() =>
        {
            var v = _newColor.Trim();
            if (v.Length == 0 || Colors.Any(c => string.Equals(c.Option.NameEn, v, StringComparison.OrdinalIgnoreCase))) { NewColor = ""; return; }
            var choice = new ColorChoice(new ColorOption(Option2IsColor ? "#9CA3AF" : "", v, v), false, false); choice.Changed += RebuildVariants; Colors.Add(choice); choice.IsSelected = true; NewColor = "";
        });
        foreach (var s in Sizes) s.Changed += RebuildVariants;
        foreach (var c in Colors) c.Changed += RebuildVariants;
        if (IsNew) RebuildVariants();
        _preview = Variants.FirstOrDefault();
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(_shell.ClosePage);
        SelectGalleryCommand = new RelayCommand(p => { _preview = (VariantEdit)p!; Raise(nameof(PreviewHex)); });
        if (_styleId is long imgId) _image = ProductImages.Get(imgId);
        AddImageCommand = new RelayCommand(PickImage);
        RemoveImageCommand = new RelayCommand(() => { _image = null; _imageBytes = null; _imageChanged = true; Raise(nameof(Image)); Raise(nameof(HasImage)); });
        PrintLabelsCommand = new RelayCommand(() => { if (_styleId is long sid) _shell.ShowDialog(new LabelPrintViewModel(_shell, [sid])); }, () => _styleId is not null);
    }

    System.Windows.Media.ImageSource? _image;
    byte[]? _imageBytes;
    bool _imageChanged;

    public System.Windows.Media.ImageSource? Image => _image ?? CategoryIcons.For(_category?.Key);
    public bool HasImage => _image is not null;
    public ICommand RemoveImageCommand { get; }

    void PickImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L["Prod_AddImage"],
            Filter = "Pictures (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _imageBytes = ProductImages.Prepare(dlg.FileName);
            _image = ProductImages.Decode(_imageBytes);
            _imageChanged = true;
            Raise(nameof(Image)); Raise(nameof(HasImage));
        }
        catch (Exception)
        {
            _shell.Toast(L["Err_Image"], BadgeKind.Danger);
        }
    }

    public Localizer L => Localizer.Instance;
    public bool IsNew { get; }
    public bool IsOwner => _shell.IsOwner;
    public string Crumb => IsNew ? L["Prod_New"] : _name;
    public IReadOnlyList<Option> Categories { get; }
    public IReadOnlyList<Option> Statuses { get; }
    public IReadOnlyList<Option> Kinds { get; }
    public IReadOnlyList<Option> Units { get; }
    Option _unit;
    public Option Unit { get => _unit; set => Set(ref _unit, value); }
    public ObservableCollection<SizeChoice> Sizes { get; }
    public ObservableCollection<ColorChoice> Colors { get; }
    public CatalogConfig Cfg { get; }
    public bool HasOption1 { get; }
    public bool HasOption2 { get; }
    public bool Option2IsColor { get; }
    public bool Option2IsText => HasOption2 && !Option2IsColor;
    public bool NoOptions => !HasOption1 && !HasOption2;
    public bool IsClothing => Cfg.BusinessType == "clothing";
    public string Option1Name => Cfg.HasOption1 ? Cfg.Option1Name! : L["Col_Size"];
    public string Option2Name => Cfg.HasOption2 ? Cfg.Option2Name! : L["Col_Color"];
    string _newSize = "", _newColor = "";
    public string NewSize { get => _newSize; set => Set(ref _newSize, value); }
    public string NewColor { get => _newColor; set => Set(ref _newColor, value); }
    public ICommand AddSizeCommand { get; }
    public ICommand AddColorCommand { get; }
    public ObservableCollection<VariantEdit> Variants { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SelectGalleryCommand { get; }
    public ICommand AddImageCommand { get; }
    public ICommand PrintLabelsCommand { get; }
    public GarmentKind PreviewKind => Enum.TryParse<GarmentKind>(_kind.Key["Kind_".Length..], out var k) ? k : GarmentKind.TShirt;
    public string PreviewHex => _preview?.ColorHex is { Length: > 0 } h ? h : "#1F2937";
    public IReadOnlyList<VariantEdit> Gallery => !Option2IsColor ? [] : Variants.Where(v => v.HasColor).GroupBy(v => v.ColorNameEn).Select(g => g.First()).Take(5).ToList();

    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public string Name { get => _name; set { if (Set(ref _name, value)) SetError(nameof(Name), null); } }
    public string NameAlt { get => _nameAlt; set => Set(ref _nameAlt, value); }
    public string Sku { get => _sku; set { if (Set(ref _sku, value)) SetError(nameof(Sku), null); } }
    public string Brand { get => _brand; set => Set(ref _brand, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public Option Category { get => _category; set { if (Set(ref _category, value)) Raise(nameof(Image)); } }
    public Option Kind { get => _kind; set { if (Set(ref _kind, value)) Raise(nameof(PreviewKind)); } }
    public bool TrackInventory { get => _track; set => Set(ref _track, value); }
    public bool AllowDiscount { get => _allowDiscount; set => Set(ref _allowDiscount, value); }
    public Option Status { get => _status; set => Set(ref _status, value); }
    public string LowStockAlert { get => _lowStock; set { if (Set(ref _lowStock, value)) SetError(nameof(LowStockAlert), null); } }
    public string TaxPercent { get => _tax; set { if (Set(ref _tax, value)) SetError(nameof(TaxPercent), null); } }
    public string BasePrice { get => _basePrice; set { if (Set(ref _basePrice, value)) { SetError(nameof(BasePrice), null); Raise(nameof(Margin)); Raise(nameof(Profit)); } } }
    public string CostPrice { get => _costPrice; set { if (Set(ref _costPrice, value)) { SetError(nameof(CostPrice), null); Raise(nameof(Margin)); Raise(nameof(Profit)); } } }

    static bool TryNum(string s, out decimal v) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v);

    public decimal Profit => TryNum(_basePrice, out var b) && TryNum(_costPrice, out var c) ? b - c : 0;
    public string Margin => TryNum(_basePrice, out var b) && b > 0 && TryNum(_costPrice, out var c) ? $"{(b - c) / b:P0}" : "—";
    public decimal TotalStock => Variants.Sum(v => Math.Max(0, v.StockValue));
    void StockChanged() => Raise(nameof(TotalStock));
    public bool VariantsEmpty => Variants.Count == 0;

    void RebuildVariants()
    {
        var keep = Variants.Where(v => !v.IsNew).ToList();
        var current = Variants.Where(v => v.IsNew).ToDictionary(v => (v.Size, v.ColorNameEn));
        var wanted = new List<VariantEdit>(keep);
        // An option nobody ticked contributes a single blank value, so a product always has at least one sellable row.
        var picked1 = HasOption1 ? Sizes.Where(s => s.IsSelected).Select(s => s.Size).ToList() : [];
        if (picked1.Count == 0) picked1.Add("");
        var picked2 = HasOption2 ? Colors.Where(c => c.IsSelected).Select(c => c.Option).ToList() : [];
        if (picked2.Count == 0) picked2.Add(new ColorOption("", "", ""));
        foreach (var c in picked2)
            foreach (var s in picked1)
            {
                if (keep.Count > 0 && s.Length == 0 && c.NameEn.Length == 0) continue;
                if (keep.Any(v => v.Size == s && v.ColorNameEn == c.NameEn)) continue;
                wanted.Add(current.TryGetValue((s, c.NameEn), out var existing)
                    ? existing
                    : new VariantEdit(s, c.Hex, c.NameEn, c.NameAr, "", null, 0, null, StockChanged));
            }

        Variants.Clear();
        foreach (var v in wanted) Variants.Add(v);
        Raise(nameof(TotalStock)); Raise(nameof(VariantsEmpty)); Raise(nameof(Gallery));
    }

    bool Validate()
    {
        SetError(nameof(Name), string.IsNullOrWhiteSpace(_name) ? L["Err_Name"] : null);
        SetError(nameof(BasePrice), !TryNum(_basePrice, out var b) || b <= 0 ? L["Err_Price"] : null);
        SetError(nameof(CostPrice), !TryNum(_costPrice, out var c) || c < 0 ? L["Err_Price"] : null);
        SetError(nameof(LowStockAlert), !int.TryParse(_lowStock, out var a) || a < 0 ? L["Err_Number"] : null);
        SetError(nameof(TaxPercent), !TryNum(_tax, out var t) || t < 0 || t > 100 ? L["Err_Number"] : null);
        if (HasErrors) { Tab = 0; return false; }
        if (Variants.Any(v => v.StockValue < 0)) { Tab = 1; _shell.Toast(L["Err_Qty"], BadgeKind.Danger); return false; }
        if (Variants.Count == 0) { Tab = 1; _shell.Toast(L["Var_EmptyMsg"], BadgeKind.Warning); return false; }
        return true;
    }

    void Save()
    {
        if (!Validate()) { if (HasErrors) _shell.Toast(L["Toast_FixErrors"], BadgeKind.Danger); return; }
        var s = _shell.S;
        var owner = _shell.User!.Id;
        try
        {
            var input = new StyleInput(_name.Trim(), string.IsNullOrWhiteSpace(_nameAlt) ? null : _nameAlt.Trim(), _category.Key, _kind.Key["Kind_".Length..],
                decimal.Parse(_basePrice, CultureInfo.InvariantCulture), decimal.Parse(_costPrice, CultureInfo.InvariantCulture), decimal.Parse(_tax, CultureInfo.InvariantCulture) / 100m,
                int.Parse(_lowStock), string.IsNullOrWhiteSpace(_brand) ? null : _brand.Trim(), string.IsNullOrWhiteSpace(_description) ? null : _description.Trim(),
                _track, _allowDiscount, _status.Key == "Status_Active",
                _unit.Key["Unit_".Length..], _unit.Key is "Unit_kg" or "Unit_g" or "Unit_m" or "Unit_l");
            var cost = input.Cost;
            long styleId;
            if (_styleId is long existing)
            {
                styleId = existing;
                s.Styles.UpdateStyle(owner, styleId, input);
            }
            else
            {
                var newOnes = Variants.Where(v => v.IsNew).Select(v => new VariantInput(v.Size, v.ColorNameEn, v.ColorNameAlt, v.ColorHex, null, null)).ToList();
                styleId = s.Styles.CreateStyle(owner, input, newOnes);
            }

            if (_imageChanged)
            {
                s.Styles.SetImage(owner, styleId, _imageBytes);
                ProductImages.Invalidate(styleId);
                _imageChanged = false;
            }

            if (_styleId is not null)
            {
                var add = Variants.Where(v => v.IsNew).Select(v => new VariantInput(v.Size, v.ColorNameEn, v.ColorNameAlt, v.ColorHex)).ToList();
                if (add.Count > 0) s.Styles.AddVariants(owner, styleId, add);
            }

            // Stock changes post as ledger entries: opening deliveries for new variants, counted adjustments for changed ones.
            var stored = s.Styles.Variants(styleId);
            foreach (var edit in Variants)
            {
                var row = edit.ProductId is long pid ? stored.First(x => x.ProductId == pid) : stored.FirstOrDefault(x => (x.Size ?? "") == edit.Size && (x.ColorNameEn ?? "") == edit.ColorNameEn);
                if (row is null) continue;
                var target = edit.StockValue;
                if (target == row.Stock) continue;
                if (edit.IsNew || row.Stock == 0 && target > 0) s.Inventory.Receive(owner, row.ProductId, target - row.Stock, cost, null, L["Prod_OpeningStock"]);
                else s.Adjustments.ApplyCount(owner, L["Prod_StockEdit"], [new CountItem(row.ProductId, target, cost)]);
            }

            _shell.ClosePage();
            _shell.Refresh();
            _shell.Toast(L["Toast_ProductSaved"]);
        }
        catch (PosException ex)
        {
            _shell.ToastError(ex);
        }
    }
}
