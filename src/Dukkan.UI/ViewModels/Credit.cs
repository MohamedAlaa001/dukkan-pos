using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

/// <summary>One editable installment line (date and amount as typed).</summary>
public sealed class InstallmentEdit : ObservableObject
{
    readonly Action _changed;
    string _due, _amount;

    public InstallmentEdit(int no, string due, decimal amount, Action changed)
    {
        No = no; _due = due; _amount = amount.ToString("0.##", CultureInfo.InvariantCulture); _changed = changed;
    }

    public int No { get; }
    public string DueDate { get => _due; set { if (Set(ref _due, value)) _changed(); } }
    public string AmountText { get => _amount; set { if (Set(ref _amount, value)) _changed(); } }
    public decimal Amount => decimal.TryParse(_amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : -1m;
    public bool DateOk => DateOnly.TryParseExact(_due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

/// <summary>
/// The credit part of checkout: where the customer stands (balance, available credit, resulting balance) and how the unpaid amount
/// will be settled — one optional due date, or an installment schedule that must add up exactly to the unpaid amount.
/// </summary>
public sealed class CreditPlanState : ObservableObject
{
    ShellViewModel? _shell;
    bool _installments;
    string _dueDate = "", _countText = "4", _firstDue = "";
    decimal _remaining, _balance, _limit, _available;
    bool _overLimit, _creditEnabled;
    string _error = "";
    readonly ObservableCollection<InstallmentEdit> _rows = [];

    public CreditPlanState() => RebuildCommand = new RelayCommand(Rebuild);

    public ObservableCollection<InstallmentEdit> Rows => _rows;
    public ICommand RebuildCommand { get; }

    public bool Installments { get => _installments; set { if (Set(ref _installments, value)) { Raise(nameof(PlainCredit)); if (value && _rows.Count == 0) Rebuild(); Validate(); } } }
    public bool PlainCredit { get => !_installments; set => Installments = !value; }
    public string DueDate { get => _dueDate; set { if (Set(ref _dueDate, value)) Validate(); } }
    public string CountText { get => _countText; set { if (Set(ref _countText, value)) Rebuild(); } }
    public string FirstDue { get => _firstDue; set { if (Set(ref _firstDue, value)) Rebuild(); } }

    public decimal Balance => _balance;
    public decimal Limit => _limit;
    public decimal Available => _available;
    public decimal Remaining => _remaining;
    public decimal Resulting => _balance + _remaining;
    public bool OverLimit => _overLimit;
    public bool CreditEnabled => _creditEnabled;
    public decimal PlanTotal => _rows.Sum(r => Math.Max(0, r.Amount));
    public string Error { get => _error; private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(IsValid)); } } }
    public bool HasError => _error.Length > 0;
    public bool IsValid => _error.Length == 0;

    public void Update(ShellViewModel shell, CustomerItem customer, decimal remaining)
    {
        _shell = shell;
        var changed = remaining != _remaining;
        _remaining = remaining;
        if (customer.Id > 0)
        {
            var p = shell.S.Credit.Position(customer.Id);
            (_balance, _limit, _available, _creditEnabled) = (p.Balance, p.Limit, p.Available, p.CreditEnabled);
        }
        else (_balance, _limit, _available, _creditEnabled) = (0m, 0m, 0m, false);
        _overLimit = remaining > 0 && _creditEnabled && remaining > _available;
        if (_firstDue.Length == 0) _firstDue = DateOnly.ParseExact(shell.S.Queries.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_installments && (changed || _rows.Count == 0)) Rebuild(); else Validate();
        Raise(nameof(Balance)); Raise(nameof(Limit)); Raise(nameof(Available)); Raise(nameof(Remaining)); Raise(nameof(Resulting));
        Raise(nameof(OverLimit)); Raise(nameof(CreditEnabled)); Raise(nameof(FirstDue));
    }

    /// <summary>Starts an installment-only plan for a known amount (used when an owner reschedules what is still owed).</summary>
    public void Configure(ShellViewModel shell, decimal remaining)
    {
        _shell = shell;
        _remaining = remaining;
        _installments = true;
        _firstDue = DateOnly.ParseExact(shell.S.Queries.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Rebuild();
        Raise(nameof(Remaining)); Raise(nameof(FirstDue)); Raise(nameof(Installments)); Raise(nameof(PlainCredit));
    }

    void Rebuild()
    {
        _rows.Clear();
        if (_remaining > 0 && int.TryParse(_countText, out var n) && n is >= 1 and <= 36
            && DateOnly.TryParseExact(_firstDue, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            try
            {
                var i = 0;
                foreach (var p in InstallmentService.EqualPlan(_remaining, n, _firstDue))
                    _rows.Add(new InstallmentEdit(++i, p.DueDate, p.Amount, Validate));
            }
            catch (PosException) { /* amount too small for that many parts: validation will explain */ }
        }
        Validate();
    }

    void Validate()
    {
        var l = Localizer.Instance;
        string err = "";
        if (_remaining > 0)
        {
            if (_installments)
            {
                if (_rows.Count == 0) err = l["Credit_PlanNeeded"];
                else if (_rows.Any(r => !r.DateOk)) err = l["Credit_BadDate"];
                else if (_rows.Any(r => r.Amount <= 0)) err = l["Err_Number"];
                else if (Money.ToCents(PlanTotal) != Money.ToCents(_remaining))
                    err = string.Format(l["Credit_PlanSum"], l.Money(_remaining), l.Money(PlanTotal));
            }
            else if (_dueDate.Length > 0 && !DateOnly.TryParseExact(_dueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                err = l["Credit_BadDate"];
        }
        Raise(nameof(PlanTotal));
        Error = err;
    }

    public CreditArrangement? ToArrangement()
    {
        if (_remaining <= 0) return null;
        if (_installments) return new CreditArrangement(null, _rows.Select(r => new InstallmentInput(r.DueDate, r.Amount)).ToList());
        return new CreditArrangement(_dueDate.Length > 0 ? _dueDate : null);
    }
}
