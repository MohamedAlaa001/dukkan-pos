using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

public sealed class CustomersViewModel : ObservableObject
{
    readonly ShellViewModel _shell;
    string _search = "";
    CustomerItem? _selected;
    CustomerDetailsViewModel? _details;
    List<CustomerItem> _all = [];

    public CustomersViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Pager = new Pager<CustomerItem>(8);
        AddCommand = new RelayCommand(() => _shell.ShowDialog(new CustomerEditViewModel(_shell, null, Load)));
        OpenCommand = new RelayCommand(p => Open((CustomerItem)p!));
        BackCommand = new RelayCommand(() => { Details = null; Load(); });
        DueCommand = new RelayCommand(() => _shell.ShowDialog(new DueReportViewModel(_shell)), () => _shell.IsOwner);
        Load();
    }

    public Localizer L => Localizer.Instance;
    public Pager<CustomerItem> Pager { get; }
    public ICommand AddCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand DueCommand { get; }
    public string Search { get => _search; set { if (Set(ref _search, value)) Apply(); } }
    public CustomerItem? Selected { get => _selected; set => Set(ref _selected, value); }
    public CustomerDetailsViewModel? Details { get => _details; private set { if (Set(ref _details, value)) { Raise(nameof(IsListVisible)); Raise(nameof(IsDetailsVisible)); } } }
    public bool IsListVisible => _details is null;
    public bool IsDetailsVisible => _details is not null;

    void Load()
    {
        _all = _shell.S.Customers.List().Select(c => new CustomerItem(c)).ToList();
        Apply();
    }

    void Open(CustomerItem c) => Details = new CustomerDetailsViewModel(_shell, c, () => { Load(); Open(_all.First(x => x.Id == c.Id)); });

    void Apply() => Pager.SetSource(_all.Where(c => string.IsNullOrWhiteSpace(_search)
        || c.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase) || c.Phone.Contains(_search, StringComparison.Ordinal)).ToList());
}

/// <summary>Add or edit a customer. Cashiers may add a basic customer; contact edits and credit settings are owner-only.</summary>
public sealed class CustomerEditViewModel : DialogViewModel
{
    readonly CustomerItem? _existing;
    readonly Action _saved;
    string _name, _phone, _email, _limit;
    bool _credit;

    public CustomerEditViewModel(ShellViewModel shell, CustomerItem? existing, Action saved) : base(shell)
    {
        _existing = existing;
        _saved = saved;
        _name = existing?.Name ?? "";
        _phone = existing?.Phone ?? "";
        _email = existing?.Email ?? "";
        _credit = existing?.CreditEnabled ?? false;
        _limit = (existing?.CreditLimit ?? 0m).ToString("0.##", CultureInfo.InvariantCulture);
    }

    public override double Width => 460;
    public bool IsNew => _existing is null;
    public bool IsOwner => Shell.IsOwner;
    public string Name { get => _name; set { if (Set(ref _name, value)) SetError(nameof(Name), null); } }
    public string Phone { get => _phone; set { if (Set(ref _phone, value)) SetError(nameof(Phone), null); } }
    public string Email { get => _email; set => Set(ref _email, value); }
    public bool CreditEnabled { get => _credit; set { Set(ref _credit, value); Raise(nameof(ShowLimit)); } }
    public string CreditLimit { get => _limit; set { if (Set(ref _limit, value)) SetError(nameof(CreditLimit), null); } }
    public bool ShowLimit => IsOwner && _credit;
    public ICommand SaveCommand => new RelayCommand(Save);

    void Save()
    {
        var l = Localizer.Instance;
        SetError(nameof(Name), string.IsNullOrWhiteSpace(_name) ? l["Err_Name"] : null);
        SetError(nameof(Phone), _phone.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(_phone, @"^01\d{9}$") ? l["Err_Phone"] : null);
        var limit = 0m;
        if (IsOwner && _credit) SetError(nameof(CreditLimit), decimal.TryParse(_limit, NumberStyles.Number, CultureInfo.InvariantCulture, out limit) && limit >= 0 ? null : l["Err_Number"]);
        if (HasErrors) return;
        try
        {
            var s = Shell.S;
            var user = Shell.User!.Id;
            var phone = string.IsNullOrWhiteSpace(_phone) ? null : _phone.Trim();
            var email = string.IsNullOrWhiteSpace(_email) ? null : _email.Trim();
            if (_existing is null)
            {
                var id = s.Customers.Add(user, _name.Trim(), phone, IsOwner && _credit, limit, email);
                _ = id;
            }
            else
            {
                s.Customers.Update(user, _existing.Id, _name, phone, email);
                s.Customers.SetCredit(user, _existing.Id, _credit, limit);
            }

            Shell.CloseDialog();
            _saved();
            Shell.Toast(l[_existing is null ? "Toast_CustomerAdded" : "Toast_Saved"]);
        }
        catch (PosException ex) { SetError(nameof(Name), Shell.ErrorText(ex)); }
    }
}

