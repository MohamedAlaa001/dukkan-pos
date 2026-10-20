using System.Collections.ObjectModel;
using System.Windows.Input;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

public sealed class IconChoice(string name)
{
    public string Name { get; } = name;
    public System.Windows.Media.ImageSource? Image { get; } = CategoryIcons.Get(name);
}

public sealed class CategoryEdit : LocalizedObject
{
    string _name, _alt, _icon;

    public CategoryEdit(CategoryRow r) { Id = r.Id; _name = r.Name; _alt = r.NameAlt ?? ""; Active = r.Active; _icon = r.Icon ?? ""; }

    public string Icon { get => _icon; set => Set(ref _icon, value ?? ""); }

    public long Id { get; }
    public bool Active { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string NameAlt { get => _alt; set => Set(ref _alt, value); }
    public string ToggleText => Localizer.Instance[Active ? "Action_Archive" : "Action_Restore"];
    public bool CanDelete => !Active;
}

/// <summary>Settings → Catalog: the shop type preset, the names of the optional product options, and the category list.</summary>
public sealed class CatalogSettingsViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    string _o1 = "", _o2 = "", _v1 = "", _v2 = "", _newCat = "", _newCatAlt = "", _message = "";
    bool _o2Color;
    string _type = "general";

    public CatalogSettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        SaveCommand = new RelayCommand(Save);
        PresetCommand = new RelayCommand(p => ApplyPreset((string)p!));
        AddCategoryCommand = new RelayCommand(AddCategory);
        SaveCategoryCommand = new RelayCommand(p => SaveCategory((CategoryEdit)p!));
        ToggleCategoryCommand = new RelayCommand(p => ToggleCategory((CategoryEdit)p!));
        DeleteCategoryCommand = new RelayCommand(p => DeleteCategory((CategoryEdit)p!));
        Load();
    }

    public Localizer L => Localizer.Instance;
    public ObservableCollection<CategoryEdit> Categories { get; } = [];
    public IReadOnlyList<IconChoice> IconChoices { get; } = CategoryIcons.Names.Select(n => new IconChoice(n)).ToList();
    string _newIcon = "";
    public string NewCategoryIcon { get => _newIcon; set => Set(ref _newIcon, value ?? ""); }
    public ICommand SaveCommand { get; }
    public ICommand PresetCommand { get; }
    public ICommand AddCategoryCommand { get; }
    public ICommand SaveCategoryCommand { get; }
    public ICommand ToggleCategoryCommand { get; }
    public ICommand DeleteCategoryCommand { get; }
    public bool HasNoCategories => Categories.Count == 0;
    public string Option1Name { get => _o1; set => Set(ref _o1, value); }
    public string Option2Name { get => _o2; set => Set(ref _o2, value); }
    public bool Option2IsColor { get => _o2Color; set => Set(ref _o2Color, value); }
    public string Option1Values { get => _v1; set => Set(ref _v1, value); }
    public string Option2Values { get => _v2; set => Set(ref _v2, value); }
    public string NewCategory { get => _newCat; set => Set(ref _newCat, value); }
    public string NewCategoryAlt { get => _newCatAlt; set => Set(ref _newCatAlt, value); }
    public string Message { get => _message; private set { Set(ref _message, value); Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;

    void Load()
    {
        var cfg = _shell.S.Config.Get();
        _type = cfg.BusinessType;
        Option1Name = cfg.Option1Name ?? ""; Option2Name = cfg.Option2Name ?? ""; Option2IsColor = cfg.Option2IsColor;
        Option1Values = string.Join(", ", cfg.Option1Presets); Option2Values = string.Join(", ", cfg.Option2Presets);
        Categories.Clear();
        var rows = _shell.S.Config.Categories(true);
        foreach (var c in rows) Categories.Add(new CategoryEdit(c));
        CategoryNames.Refresh(rows);
        Raise(nameof(HasNoCategories));

    }

    static string[] Split(string s) => s.Split([',', '،', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    void Save()
    {
        try
        {
            _shell.S.Config.Save(_shell.User!.Id, new CatalogConfig(_type, string.IsNullOrWhiteSpace(_o1) ? null : _o1.Trim(), string.IsNullOrWhiteSpace(_o2) ? null : _o2.Trim(),
                _o2Color, Split(_v1), Split(_v2)));
            Message = L["Toast_Saved"];
        }
        catch (PosException ex) { Message = _shell.ErrorText(ex); }
    }

    void ApplyPreset(string type) =>
        _shell.Confirm(L["Cat_PresetTitle"], L["Cat_PresetNote"], L["Cat_PresetApply"], false, () =>
        {
            try { _shell.S.Config.ApplyPreset(_shell.User!.Id, type, L.IsArabic); Load(); Message = L["Toast_Saved"]; }
            catch (PosException ex) { Message = _shell.ErrorText(ex); }
        });

    void AddCategory()
    {
        if (string.IsNullOrWhiteSpace(_newCat)) { Message = L["Err_Name"]; return; }
        try
        {
            _shell.S.Config.AddCategory(_shell.User!.Id, _newCat, _newCatAlt, _newIcon);
            NewCategory = ""; NewCategoryAlt = ""; NewCategoryIcon = "";
            Load(); Message = L["Toast_Saved"];
        }
        catch (PosException ex) { Message = _shell.ErrorText(ex); }
    }

    void SaveCategory(CategoryEdit c)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) { Message = L["Err_Name"]; return; }
        try { _shell.S.Config.RenameCategory(_shell.User!.Id, c.Id, c.Name, c.NameAlt, c.Icon); Load(); Message = L["Toast_Saved"]; }
        catch (PosException ex) { Message = _shell.ErrorText(ex); Load(); }
    }

    void ToggleCategory(CategoryEdit c)
    {
        try { _shell.S.Config.SetCategoryActive(_shell.User!.Id, c.Id, !c.Active); Load(); }
        catch (PosException ex) { Message = _shell.ErrorText(ex); }
    }

    /// <summary>Delete is offered only for archived categories, and only goes through when no product still uses it.</summary>
    void DeleteCategory(CategoryEdit c)
    {
        if (_shell.S.Config.CategoryUsage(c.Id) > 0) { Message = L["Cat_InUse"]; return; }
        _shell.Confirm(L["Cat_DeleteTitle"], $"{c.Name}\n{L["Cat_DeleteNote"]}", L["Cat_Delete"], true, () =>
        {
            try { _shell.S.Config.DeleteCategory(_shell.User!.Id, c.Id); Load(); Message = L["Cat_Deleted"]; }
            catch (PosException ex) { Message = _shell.ErrorText(ex); Load(); }
        });
    }
}
