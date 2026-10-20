using System.Globalization;
using System.Windows.Input;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

public sealed class ExpensesViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    List<ExpenseItem> _all = [];

    public ExpensesViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Pager = new Pager<ExpenseItem>(8);
        AddCommand = new RelayCommand(() => _shell.ShowDialog(new AddExpenseViewModel(_shell, Load)));
        CashCommand = new RelayCommand(() => _shell.ShowDialog(new CashMovementViewModel(_shell, Load)));
        Load();
    }

    public Localizer L => Localizer.Instance;
    public Pager<ExpenseItem> Pager { get; }
    public ICommand AddCommand { get; }
    public ICommand CashCommand { get; }
    public decimal MonthTotal => _all.Sum(e => e.Amount);
    public decimal TodayTotal => _all.Where(e => e.Date.Date == DateTime.Parse(_shell.S.Queries.Today, CultureInfo.InvariantCulture)).Sum(e => e.Amount);
    public decimal RegisterTotal => _all.Where(e => e.FromRegister).Sum(e => e.Amount);

    void Load()
    {
        var today = DateTime.ParseExact(_shell.S.Queries.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var first = new DateTime(today.Year, today.Month, 1);
        _all = _shell.S.Queries.Expenses(first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Select(e => new ExpenseItem(e)).ToList();
        Pager.SetSource(_all);
        Raise(nameof(MonthTotal)); Raise(nameof(TodayTotal)); Raise(nameof(RegisterTotal));
    }
}

public sealed class AddExpenseViewModel : DialogViewModel
{
    readonly Action _saved;
    string _amount = "", _desc = "";
    bool _fromRegister;

    public AddExpenseViewModel(ShellViewModel shell, Action saved) : base(shell)
    {
        _saved = saved;
        Categories = new[] {"Exp_Rent", "Exp_Utilities", "Exp_Salaries", "Exp_Supplies", "Exp_Marketing", "Exp_Transport", "Exp_Other"}.Select(k => new Option(k)).ToList();
        _fromRegister = shell.HasShift;
    }

    public override double Width => 480;
    public IReadOnlyList<Option> Categories { get; }
    Option? _category;
    public Option? Category { get => _category; set { if (Set(ref _category, value)) SetError(nameof(Category), null); } }
    public string Amount { get => _amount; set { if (Set(ref _amount, value)) SetError(nameof(Amount), null); } }
    public string Description { get => _desc; set { if (Set(ref _desc, value)) SetError(nameof(Description), null); } }
    public bool FromRegister { get => _fromRegister; set => Set(ref _fromRegister, value); }
    public bool CanUseRegister => Shell.HasShift;
    public ICommand SaveCommand => new RelayCommand(Save);

    void Save()
    {
        var l = Localizer.Instance;
        SetError(nameof(Category), Category is null ? l["Err_Choose"] : null);
        var ok = decimal.TryParse(_amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0;
        SetError(nameof(Amount), ok ? null : l["Err_Number"]);
        SetError(nameof(Description), string.IsNullOrWhiteSpace(_desc) ? l["Err_Required"] : null);
        if (_fromRegister && !Shell.HasShift) SetError(nameof(Amount), l["Toast_NeedShift"]);
        if (HasErrors) return;
        try
        {
            Shell.S.Cash.RecordExpense(Shell.User!.Id, Shell.S.Queries.Today, Category!.Key["Exp_".Length..], v, _desc.Trim(), _fromRegister, _fromRegister ? Shell.Shift!.Id : null);
            Shell.CloseDialog();
            _saved();
            Shell.Toast(l["Toast_ExpenseAdded"]);
        }
        catch (PosException ex) { SetError(nameof(Amount), Shell.ErrorText(ex)); }
    }
}

/// <summary>Owner cash-in / cash-out of the drawer — recorded as drawer movements, never as sales or expenses.</summary>
public sealed class CashMovementViewModel : DialogViewModel
{
    readonly Action _saved;
    string _amount = "", _reason = "";
    bool _cashIn = true;

    public CashMovementViewModel(ShellViewModel shell, Action saved) : base(shell) => _saved = saved;

    public override double Width => 440;
    public bool CashIn { get => _cashIn; set { if (Set(ref _cashIn, value)) Raise(nameof(CashOut)); } }
    public bool CashOut { get => !_cashIn; set => CashIn = !value; }
    public string Amount { get => _amount; set { if (Set(ref _amount, value)) SetError(nameof(Amount), null); } }
    public string Reason { get => _reason; set { if (Set(ref _reason, value)) SetError(nameof(Reason), null); } }
    public ICommand SaveCommand => new RelayCommand(Save);

    void Save()
    {
        var l = Localizer.Instance;
        var ok = decimal.TryParse(_amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0;
        SetError(nameof(Amount), ok ? null : l["Err_Number"]);
        SetError(nameof(Reason), string.IsNullOrWhiteSpace(_reason) ? l["Err_Required"] : null);
        if (!Shell.HasShift) SetError(nameof(Amount), l["Toast_NeedShift"]);
        if (HasErrors) return;
        try
        {
            Shell.S.Cash.RecordOwnerCash(Shell.User!.Id, Shell.Shift!.Id, _cashIn, v, _reason.Trim());
            Shell.CloseDialog();
            _saved();
            Shell.Toast(l["Toast_Saved"]);
        }
        catch (PosException ex) { SetError(nameof(Amount), Shell.ErrorText(ex)); }
    }
}
