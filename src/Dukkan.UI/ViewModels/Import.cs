using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

/// <summary>Controlled CSV import: choose a UTF-8 file, review row-specific problems, then apply under a migration reference.</summary>
public sealed class ImportViewModel : DialogViewModel
{
    readonly Action _done;
    ImportKind _kind = ImportKind.Products;
    string? _csv, _file;
    string _reference = "", _summary = "", _message = "";
    bool _canApply;

    public ImportViewModel(ShellViewModel shell, Action done) : base(shell)
    {
        _done = done;
        ChooseCommand = new RelayCommand(Choose);
        TemplateCommand = new RelayCommand(SaveTemplate);
        ApplyCommand = new RelayCommand(Apply, () => _canApply);
        CloseOnboardingCommand = new RelayCommand(CloseOnboarding, () => Onboarding);
    }

    public override double Width => 700;
    public ObservableCollection<ImportError> Errors { get; } = [];
    public ICommand ChooseCommand { get; }
    public ICommand TemplateCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CloseOnboardingCommand { get; }
    public bool Onboarding => Shell.S.Imports.IsOnboardingOpen();
    public bool ProductsMode { get => _kind == ImportKind.Products; set { if (value) SetKind(ImportKind.Products); } }
    public bool CustomersMode { get => _kind == ImportKind.Customers; set { if (value) SetKind(ImportKind.Customers); } }
    public string FileName => _file is null ? "" : Path.GetFileName(_file);
    public string Reference { get => _reference; set { if (Set(ref _reference, value)) { SetError(nameof(Reference), null); Refresh(); } } }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Message { get => _message; private set { Set(ref _message, value); Raise(nameof(HasMessage)); } }
    public bool HasMessage => _message.Length > 0;
    public bool HasErrors2 => Errors.Count > 0;

    void SetKind(ImportKind k)
    {
        _kind = k; _csv = null; _file = null; Errors.Clear(); Summary = ""; Message = ""; _canApply = false;
        Raise(nameof(ProductsMode)); Raise(nameof(CustomersMode)); Raise(nameof(FileName)); Raise(nameof(HasErrors2));
        CommandManager.InvalidateRequerySuggested();
    }

    void SaveTemplate()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV|*.csv", FileName = _kind == ImportKind.Products ? "dukkan-products-template.csv" : "dukkan-customers-template.csv" };
        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, ImportService.Template(_kind), new UTF8Encoding(true)); Message = Localizer.Instance["Imp_TemplateSaved"]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = Localizer.Instance["Toast_CopyFailed"]; }
    }

    void Choose()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "CSV|*.csv;*.txt" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _csv = File.ReadAllText(dlg.FileName, new UTF8Encoding(false, true));
            _file = dlg.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            _csv = null; Message = Localizer.Instance["Imp_BadFile"]; return;
        }
        Raise(nameof(FileName));
        Refresh();
    }

    void Refresh()
    {
        Errors.Clear();
        Message = "";
        if (_csv is null) { _canApply = false; Summary = ""; return; }
        var l = Localizer.Instance;
        var p = Shell.S.Imports.Preview(_kind, _csv);
        foreach (var e in p.Errors.Take(200)) Errors.Add(e with { Message = l.Has("Imp_Err_" + e.Code) ? l["Imp_Err_" + e.Code] : e.Message });
        Summary = _kind == ImportKind.Products
            ? string.Format(l["Imp_SummaryProducts"], p.Rows, p.Styles, p.Variants, p.OpeningUnits, l.Money(p.OpeningValue))
            : string.Format(l["Imp_SummaryCustomers"], p.Rows, p.Customers, l.Money(p.OpeningBalances));
        _canApply = p.IsValid && _reference.Trim().Length > 0;
        Raise(nameof(HasErrors2));
        CommandManager.InvalidateRequerySuggested();
    }

    void Apply()
    {
        var l = Localizer.Instance;
        if (_csv is null) return;
        if (string.IsNullOrWhiteSpace(_reference)) { SetError(nameof(Reference), l["Err_Required"]); return; }
        try
        {
            var r = Shell.S.Imports.Apply(Shell.User!.Id, _kind, _csv, _reference);
            Shell.CloseDialog();
            _done();
            Shell.Refresh();
            Shell.Toast(string.Format(l[r.AlreadyApplied ? "Imp_AlreadyApplied" : "Imp_Done"], r.Rows));
        }
        catch (PosException ex) { Message = ex.Code is PosError.PermissionDenied or PosError.Duplicate ? Shell.ErrorText(ex) : ex.Message; }
    }

    void CloseOnboarding() =>
        Shell.ShowDialog(new ConfirmViewModel(Shell, Localizer.Instance["Imp_CloseOnboarding"], Localizer.Instance["Imp_CloseOnboardingNote"], Localizer.Instance["Imp_CloseOnboarding"], true, () =>
        {
            try { Shell.S.Imports.CloseOnboarding(Shell.User!.Id); Shell.Toast(Localizer.Instance["Toast_Saved"]); }
            catch (PosException ex) { Shell.ToastError(ex); }
        }));
}
