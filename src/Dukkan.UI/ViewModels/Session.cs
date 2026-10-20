using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;

namespace Dukkan.UI;

public enum AppMode { Setup, Login, Main }

public sealed class ShellViewModel : ObservableObject
{
    readonly Dictionary<string, object> _keepPages = [];
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    string _nav = "Dashboard";
    AppMode _mode;
    UserInfo? _user;
    ShiftInfo? _shift;
    object? _page;
    object? _dialog;
    bool _notificationsOpen;
    string _toastText = "";
    BadgeKind _toastKind = BadgeKind.Success;
    bool _toastVisible;
    string _globalSearch = "";

    public ShellViewModel(AppServices services)
    {
        S = services;
        Login = new LoginViewModel(this);
        Setup = new SetupViewModel(this);
        Localizer.Instance.StoreName = services.Settings.GetValue("store_name") ?? "";
        CategoryNames.Refresh(services.Config.Categories(true));

        Notifications = [];
        ToggleLanguageCommand = new RelayCommand(() => SetArabic(!Localizer.Instance.IsArabic));
        SignOutCommand = new RelayCommand(SignOut);
        LockCommand = new RelayCommand(Lock);
        ToggleNotificationsCommand = new RelayCommand(() => { RefreshNotifications(); NotificationsOpen = !NotificationsOpen; });
        NavigateCommand = new RelayCommand(p => Nav = (string)p!);
        SearchCommand = new RelayCommand(() => ShowDialog(new GlobalSearchViewModel(this, GlobalSearch.Trim())), () => IsMainMode && !HasDialog && !string.IsNullOrWhiteSpace(GlobalSearch));
        DismissToastCommand = new RelayCommand(() => ToastVisible = false);
        OpenShiftCommand = new RelayCommand(() => ShowDialog(new OpenShiftViewModel(this)), () => User is not null && Shift is null);
        CloseShiftCommand = new RelayCommand(() => ShowDialog(new CloseShiftViewModel(this)), () => User is not null && Shift is not null && CanCloseShift);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastVisible = false; };
        Mode = S.NeedsSetup ? AppMode.Setup : AppMode.Login;
        RestoreLanguage();
    }

    public AppServices S { get; }
    public Localizer L => Localizer.Instance;
    public LoginViewModel Login { get; }
    public SetupViewModel Setup { get; }
    public ObservableCollection<NotificationItem> Notifications { get; }
    public ICommand ToggleLanguageCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand LockCommand { get; }
    public ICommand ToggleNotificationsCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand DismissToastCommand { get; }
    public ICommand OpenShiftCommand { get; }
    public ICommand CloseShiftCommand { get; }

    public AppMode Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value)) { Raise(nameof(IsSetupMode)); Raise(nameof(IsLoginMode)); Raise(nameof(IsMainMode)); } }
    }

    public bool IsSetupMode => _mode == AppMode.Setup;
    public bool IsLoginMode => _mode == AppMode.Login;
    public bool IsMainMode => _mode == AppMode.Main;

    public UserInfo? User { get => _user; private set { if (Set(ref _user, value)) { Raise(nameof(IsOwner)); Raise(nameof(UserName)); Raise(nameof(UserRole)); } } }
    public bool IsOwner => _user?.Role == Role.Owner;
    public string UserName => _user?.DisplayName ?? "";
    public string UserRole => L[IsOwner ? "Role_Owner" : "Role_Cashier"];

    public ShiftInfo? Shift
    {
        get => _shift;
        private set { if (Set(ref _shift, value)) { Raise(nameof(HasShift)); Raise(nameof(NoShift)); Raise(nameof(ShiftText)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public bool HasShift => _shift is not null;
    public bool NoShift => _shift is null;
    public bool CanCloseShift => _user is { } u && _shift is { } sh && (u.Role == Role.Owner || sh.CashierId == u.Id);

    public string ShiftText => _shift is null
        ? L["Shift_None"]
        : string.Format(L["Shift_Open"], DateTimeOffset.Parse(_shift.OpenedAt, CultureInfo.InvariantCulture).ToString("HH:mm", CultureInfo.InvariantCulture), _shift.CashierName);

    public object? Page { get => _page; private set => Set(ref _page, value); }
    public object? Dialog { get => _dialog; private set { if (Set(ref _dialog, value)) Raise(nameof(HasDialog)); } }
    public bool HasDialog => _dialog is not null;
    public bool NotificationsOpen { get => _notificationsOpen; set => Set(ref _notificationsOpen, value); }
    public string ToastText { get => _toastText; private set => Set(ref _toastText, value); }
    public BadgeKind ToastKind { get => _toastKind; private set { if (Set(ref _toastKind, value)) Raise(nameof(ToastGlyph)); } }
    public string ToastGlyph => _toastKind switch { BadgeKind.Danger => Icons.Close, BadgeKind.Warning => Icons.Warning, BadgeKind.Info => Icons.Info, _ => Icons.Check };
    public bool ToastVisible { get => _toastVisible; set => Set(ref _toastVisible, value); }
    public string GlobalSearch { get => _globalSearch; set => Set(ref _globalSearch, value); }
    public int UnreadCount => Notifications.Count;

    // ---------------------------------------------------------------- navigation
    static readonly HashSet<string> CashierPages = ["Pos", "Products", "Customers", "Inventory"];

    public bool CanAccess(string key) => IsOwner || CashierPages.Contains(key);

    public string Nav
    {
        get => _nav;
        set
        {
            if (!CanAccess(value)) { Toast(L["Toast_OwnerOnly"], BadgeKind.Warning); Raise(); return; }
            if (_nav == value && _page is not ProductEditViewModel) return;
            _nav = value;
            Raise();
            NotificationsOpen = false;
            Page = PageFor(value);
        }
    }

    object PageFor(string key)
    {
        // The POS keeps its cart while the cashier looks at other screens; everything else reloads fresh data.
        if (key == "Pos" && _keepPages.TryGetValue(key, out var kept))
        {
            ((PosViewModel)kept).RefreshCatalog();
            return kept;
        }
        object vm = key switch
        {
            "Dashboard" => new DashboardViewModel(this),
            "Pos" => new PosViewModel(this),
            "Products" => new ProductsViewModel(this),
            "Inventory" => new StockWorkspaceViewModel(this),
            "Customers" => new CustomersViewModel(this),
            "Expenses" => new ExpensesViewModel(this),
            "Suppliers" => new SuppliersViewModel(this),
            "Reports" => new ReportsViewModel(this),
            "Settings" => new SettingsViewModel(this),
            _ => new DashboardViewModel(this),
        };
        if (key == "Pos") _keepPages[key] = vm;
        return vm;
    }

    public void ShowPage(object vm) { NotificationsOpen = false; Page = vm; }

    public void OpenProductEditor(long? styleId) => ShowPage(new ProductEditViewModel(this, styleId));

    public void ClosePage() => Page = PageFor(_nav);

    /// <summary>Reloads the current page (after data changed elsewhere) and refreshes the shift chip and notifications.</summary>
    public void Refresh()
    {
        RefreshShift();
        RefreshNotifications();
        if (Page is ReportsViewModel reports) reports.Reload();
    }

    // ---------------------------------------------------------------- dialogs and toasts
    public void ShowDialog(DialogViewModel vm) { NotificationsOpen = false; Dialog = vm; }

    public void CloseDialog() => Dialog = null;

    public void Confirm(string title, string message, string confirmText, bool danger, Action onConfirm) =>
        ShowDialog(new ConfirmViewModel(this, title, message, confirmText, danger, onConfirm));

    public void Toast(string message, BadgeKind kind = BadgeKind.Success)
    {
        ToastText = message;
        ToastKind = kind;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Shows a business-rule error in the user's language (falling back to the service message).</summary>
    public void ToastError(PosException ex) => Toast(ErrorText(ex), BadgeKind.Danger);

    public string ErrorText(PosException ex)
    {
        var key = "Err_" + ex.Code;
        var text = L[key];
        return text == key ? ex.Message : text;
    }

    // ---------------------------------------------------------------- session
    public void SignedIn(UserInfo user)
    {
        User = user;
        Mode = AppMode.Main;
        RefreshShift();
        _nav = "";
        Nav = IsOwner ? "Dashboard" : "Pos";
        RefreshNotifications();
        Toast(string.Format(L["Toast_Welcome"], user.DisplayName), BadgeKind.Info);
    }

    void SignOut()
    {
        CloseDialog();
        NotificationsOpen = false;
        _keepPages.Clear();
        User = null;
        Page = null;
        Mode = AppMode.Login;
        Login.Reset();
    }

    /// <summary>Lock keeps the cart and open shift; the same or another user signs in to continue.</summary>
    void Lock()
    {
        CloseDialog();
        NotificationsOpen = false;
        User = null;
        Mode = AppMode.Login;
        Login.Reset();
    }

    public void FinishSetup(string username)
    {
        Login.Username = username;
        Mode = AppMode.Login;
    }

    public void RefreshShift() => Shift = S.Queries.CurrentShift();

    // ---------------------------------------------------------------- notifications
    public void RefreshNotifications()
    {
        Notifications.Clear();
        if (User is null) return;
        var styles = S.Styles.List();
        var low = styles.Count(x => x.State == LocalCashier.Persistence.StockStatus.LowStock);
        var empty = styles.Count(x => x.State == LocalCashier.Persistence.StockStatus.OutOfStock);
        if (low > 0) Notifications.Add(new NotificationItem { TitleKey = "Notif_LowStock", DetailKey = "Notif_LowStockN", Args = [low], Kind = BadgeKind.Warning, Glyph = Icons.Warning });
        if (empty > 0) Notifications.Add(new NotificationItem { TitleKey = "Notif_OutOfStock", DetailKey = "Notif_OutOfStockN", Args = [empty], Kind = BadgeKind.Danger, Glyph = Icons.Error });
        var jobs = S.PrintJobs.Attention();
        if (jobs.Count > 0) Notifications.Add(new NotificationItem { TitleKey = "Notif_Print", DetailKey = "Notif_PrintN", Args = [jobs.Count], Kind = BadgeKind.Warning, Glyph = Icons.Print });
        var held = S.Held.List().Count;
        if (held > 0) Notifications.Add(new NotificationItem { TitleKey = "Notif_Held", DetailKey = "Notif_HeldN", Args = [held], Kind = BadgeKind.Info, Glyph = Icons.Pause });
        if (IsOwner)
        {
            var expiry = S.Stock.NearExpiry().Count;
            if (expiry > 0) Notifications.Add(new NotificationItem { TitleKey="Stock_Expiry",DetailKey="Stock_ExpiryCount",Args=[expiry],Kind=BadgeKind.Warning,Glyph=Icons.Warning });
            var overdueOrders = S.Suppliers.OrderList(openOnly: true).Count(o => o.Overdue);
            if (overdueOrders > 0) Notifications.Add(new NotificationItem { TitleKey = "PO_OverdueTitle", DetailKey = "PO_OverdueCount", Args = [overdueOrders], Kind = BadgeKind.Warning, Glyph = Icons.Calendar });
            var b = BackupWarning();
            if (b is not null) Notifications.Add(new NotificationItem { TitleKey = "Notif_Backup", DetailKey = b, Args = [], Kind = BadgeKind.Danger, Glyph = Icons.Warning });
        }

        Raise(nameof(UnreadCount));
    }

    /// <summary>Key of the strongest backup warning, or null when local and external backups are healthy.</summary>
    public string? BackupWarning()
    {
        var st = S.Backup.Status();
        if (!S.Secrets.Exists) return "Notif_BackupNoSecret";
        if (st.ExternalFailure is not null || st.LocalFailure is not null) return "Notif_BackupFailed";
        if (string.IsNullOrWhiteSpace(S.Settings.GetValue("backup_external_dir"))) return "Notif_BackupNoDir";
        if (st.LastExternal is null || S.Clock.Now - st.LastExternal > TimeSpan.FromHours(24)) return "Notif_BackupOld";
        return null;
    }

    // ---------------------------------------------------------------- language
    string LangFile => Path.Combine(S.DataDir, "language.txt");

    void RestoreLanguage()
    {
        try
        {
            if (File.Exists(LangFile)) Localizer.Instance.IsArabic = File.ReadAllText(LangFile).Trim() == "ar";
        }
        catch (IOException) { /* keep the default language */ }
    }

    public void SetArabic(bool arabic)
    {
        Localizer.Instance.IsArabic = arabic;
        try { File.WriteAllText(LangFile, arabic ? "ar" : "en"); } catch (IOException) { /* preference only */ }
        Raise(nameof(UserRole)); Raise(nameof(ShiftText));
        if (User is not null) RefreshNotifications();
    }

    public static CultureInfo Culture => new(Localizer.Instance.IsArabic ? "ar-EG" : "en-US");

    /// <summary>Runs <paramref name="apply"/> after a short delay while <paramref name="setLoading"/> shows a loading state.</summary>
    public static void SimulateLoading(Action<bool> setLoading, Action apply, int ms = 350)
    {
        setLoading(true);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); apply(); setLoading(false); };
        t.Start();
    }
}

public sealed class LoginViewModel(ShellViewModel shell) : ValidatableViewModel
{
    string _username = "";
    string _password = "";
    bool _remember;
    bool _showPassword;
    bool _busy;
    string _message = "";

    public string Username { get => _username; set { if (Set(ref _username, value)) { SetError(nameof(Username), null); Message = ""; } } }

    public string Password { get => _password; set { if (Set(ref _password, value)) { SetError(nameof(Password), null); Message = ""; } } }

    public bool RememberMe { get => _remember; set => Set(ref _remember, value); }

    public bool ShowPassword { get => _showPassword; set => Set(ref _showPassword, value); }

    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) Raise(nameof(NotBusy)); } }

    public bool NotBusy => !_busy;

    /// <summary>Wrong password / locked messages (shown as text, not color alone).</summary>
    public string Message { get => _message; private set => Set(ref _message, value); }

    public string EyeGlyph => _showPassword ? Icons.EyeOff : Icons.Eye;

    public ICommand ToggleShowCommand => new RelayCommand(() => { ShowPassword = !ShowPassword; Raise(nameof(EyeGlyph)); });

    public ICommand SignInCommand => new RelayCommand(SignIn, () => !_busy);

    public ICommand ForgotCommand => new RelayCommand(() => shell.ShowDialog(new RecoveryResetViewModel(shell, Username)));

    public void Reset() { Password = ""; IsBusy = false; ShowPassword = false; Message = ""; Raise(nameof(EyeGlyph)); }

    void SignIn()
    {
        var l = Localizer.Instance;
        SetError(nameof(Username), string.IsNullOrWhiteSpace(Username) ? l["Err_Username"] : null);
        SetError(nameof(Password), string.IsNullOrEmpty(Password) ? l["Err_Password"] : null);
        if (HasErrors) return;
        var result = shell.S.Users.Login(Username.Trim(), Password);
        Password = "";
        switch (result.Failure)
        {
            case AuthFailure.None:
                shell.SignedIn(result.User!);
                break;
            case AuthFailure.Locked:
                Message = string.Format(l["Login_Locked"], result.LockedUntil?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
                break;
            default:
                Message = result.AttemptsLeft > 0 ? string.Format(l["Login_Bad"], result.AttemptsLeft) : l["Login_BadShort"];
                break;
        }
    }
}

// ===================================================================== first-run setup
public sealed class SetupViewModel(ShellViewModel shell) : ValidatableViewModel
{
    string _store = "", _phone = "", _address = "", _ownerName = "", _username = "owner", _password = "", _confirm = "";
    string _secret = "", _secretConfirm = "", _taxPercent = "0";
    string? _taxChoice;
    string _business = "general";
    int _step;
    IReadOnlyList<string> _codes = [];

    public string StoreName { get => _store; set { if (Set(ref _store, value)) SetError(nameof(StoreName), null); } }
    string _summary = "";
    public string Summary { get => _summary; private set { Set(ref _summary, value); Raise(nameof(HasSummary)); } }
    public bool HasSummary => _summary.Length > 0;
    public string StorePhone { get => _phone; set => Set(ref _phone, value); }
    public string StoreAddress { get => _address; set => Set(ref _address, value); }
    public string OwnerName { get => _ownerName; set { if (Set(ref _ownerName, value)) SetError(nameof(OwnerName), null); } }
    public string Username { get => _username; set { if (Set(ref _username, value)) SetError(nameof(Username), null); } }
    public string Password { get => _password; set { if (Set(ref _password, value)) SetError(nameof(Password), null); } }
    public string ConfirmPassword { get => _confirm; set { if (Set(ref _confirm, value)) SetError(nameof(ConfirmPassword), null); } }
    public string BackupSecret { get => _secret; set { if (Set(ref _secret, value)) SetError(nameof(BackupSecret), null); } }
    public string BackupSecretConfirm { get => _secretConfirm; set { if (Set(ref _secretConfirm, value)) SetError(nameof(BackupSecret), null); } }
    public string TaxPercent { get => _taxPercent; set { if (Set(ref _taxPercent, value)) SetError(nameof(TaxPercent), null); } }
    public string BusinessType { get => _business; set => Set(ref _business, value ?? "general"); }
    public string? TaxChoice { get => _taxChoice; set { if (Set(ref _taxChoice, value)) SetError(nameof(TaxChoice), null); } }
    public int Step { get => _step; private set { if (Set(ref _step, value)) { Raise(nameof(IsFormStep)); Raise(nameof(IsCodesStep)); } } }
    public bool IsFormStep => _step == 0;
    public bool IsCodesStep => _step == 1;
    public IReadOnlyList<string> Codes { get => _codes; private set => Set(ref _codes, value); }
    public string FirstUsername { get; private set; } = "owner";

    public ICommand CreateCommand => new RelayCommand(Create);
    public ICommand ContinueCommand => new RelayCommand(() => shell.FinishSetup(FirstUsername));
    public ICommand CopyCodesCommand => new RelayCommand(() =>
    {
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Codes)); shell.Toast(Localizer.Instance["Toast_Copied"], BadgeKind.Info); }
        catch (System.Runtime.InteropServices.COMException) { shell.Toast(Localizer.Instance["Toast_CopyFailed"], BadgeKind.Warning); }
    });

    bool Validate()
    {
        var l = Localizer.Instance;
        SetError(nameof(StoreName), string.IsNullOrWhiteSpace(_store) ? l["Err_Required"] : null);
        SetError(nameof(OwnerName), string.IsNullOrWhiteSpace(_ownerName) ? l["Err_Required"] : null);
        SetError(nameof(Username), string.IsNullOrWhiteSpace(_username) ? l["Err_Required"] : null);
        SetError(nameof(Password), _password.Length < UserService.MinPasswordLength ? string.Format(l["Err_WeakPassword"], UserService.MinPasswordLength) : null);
        SetError(nameof(ConfirmPassword), _confirm != _password ? l["Err_PasswordMismatch"] : null);

        SetError(nameof(TaxChoice), _taxChoice is null ? l["Err_ChooseTax"] : null);
        var okTax = decimal.TryParse(_taxPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) && pct is >= 0 and <= 100;
        SetError(nameof(TaxPercent), okTax ? null : l["Err_Number"]);
        var anySecret = _secret.Length > 0 || _secretConfirm.Length > 0;
        SetError(nameof(BackupSecret), anySecret && (_secret.Length < 10 || _secret != _secretConfirm) ? l["Err_Secret"] : null);
        Summary = HasErrors ? l["Setup_FixFields"] : "";
        return !HasErrors;
    }

    void Create()
    {
        if (!Validate()) return;
        var s = shell.S;
        try
        {
            var owner = s.Users.Create(_username.Trim(), _ownerName.Trim(), Role.Owner, _password);
            s.Settings.ConfigureTax(owner, _taxChoice == "Included");
            FirstUsername = _username.Trim();
            s.Config.ApplyPreset(owner, _business, Localizer.Instance.IsArabic);

            s.Settings.SetValue(owner, "store_name", _store.Trim());
            Localizer.Instance.StoreName = _store.Trim();
            s.Settings.SetValue(owner, "store_phone", _phone.Trim());
            s.Settings.SetValue(owner, "store_address", _address.Trim());
            s.Settings.SetValue(owner, "default_tax_percent", _taxPercent.Trim());
            s.Settings.SetValue(owner, "receipt_language", Localizer.Instance.IsArabic ? "ar" : "en");
            if (_secret.Length >= 10) s.Secrets.Save(_secret);
            Codes = s.Users.GenerateRecoveryCodes(owner);
            Password = ""; ConfirmPassword = ""; BackupSecret = ""; BackupSecretConfirm = "";
            Step = 1;
        }
        catch (PosException ex)
        {
            SetError(nameof(Password), shell.ErrorText(ex));
        }
    }
}

// ===================================================================== shift dialogs
public sealed class OpenShiftViewModel : DialogViewModel
{
    string _float = "500";

    public OpenShiftViewModel(ShellViewModel shell) : base(shell) { }

    public override double Width => 400;
    public string Float { get => _float; set { if (Set(ref _float, value)) SetError(nameof(Float), null); } }
    public ICommand OpenCommand => new RelayCommand(Open);

    void Open()
    {
        var l = Localizer.Instance;
        if (!decimal.TryParse(_float, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v < 0) { SetError(nameof(Float), l["Err_Number"]); return; }
        try
        {
            Shell.S.Shifts.Open(Shell.User!.Id, v);
            Shell.CloseDialog();
            Shell.Refresh();
            Shell.Toast(l["Toast_ShiftOpened"]);
        }
        catch (PosException ex) { SetError(nameof(Float), Shell.ErrorText(ex)); }
    }
}

/// <summary>Blind close: the count is entered first; only then are expected cash and variance shown.</summary>
public sealed class CloseShiftViewModel : DialogViewModel
{
    string _counted = "";
    string _reason = "";
    int _stage;
    decimal _expected;
    decimal _countedValue;

    public CloseShiftViewModel(ShellViewModel shell) : base(shell) { }

    public override double Width => 440;
    public string Counted { get => _counted; set { if (Set(ref _counted, value)) SetError(nameof(Counted), null); } }
    public string Reason { get => _reason; set { if (Set(ref _reason, value)) SetError(nameof(Reason), null); } }
    public bool IsCountStage => _stage == 0;
    public bool IsReviewStage => _stage == 1;
    public decimal Expected => _expected;
    public decimal CountedValue => _countedValue;
    public decimal Variance => _countedValue - _expected;
    public bool HasVariance => Variance != 0;
    public IReadOnlyList<PaymentTotalsItem> PaymentTotals=>Shell.Shift is null?[]:Shell.S.Reports.PaymentBreakdown("","",shiftId:Shell.Shift.Id).Select(p=>new PaymentTotalsItem(p)).ToList();
    public ICommand CountCommand => new RelayCommand(Count);
    public ICommand BackCommand => new RelayCommand(() => { _stage = 0; Raise(nameof(IsCountStage)); Raise(nameof(IsReviewStage)); });
    public ICommand CloseShiftCommand => new RelayCommand(Close);

    void Count()
    {
        var l = Localizer.Instance;
        if (!decimal.TryParse(_counted, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v < 0) { SetError(nameof(Counted), l["Err_Number"]); return; }
        _countedValue = v;
        _expected = Shell.S.Shifts.ExpectedCash(Shell.Shift!.Id);
        _stage = 1;
        Raise(nameof(IsCountStage)); Raise(nameof(IsReviewStage)); Raise(nameof(Expected)); Raise(nameof(CountedValue)); Raise(nameof(Variance)); Raise(nameof(HasVariance));
    }

    void Close()
    {
        var l = Localizer.Instance;
        if (HasVariance && string.IsNullOrWhiteSpace(_reason)) { SetError(nameof(Reason), l["Err_VarianceReason"]); return; }
        try
        {
            var summaryDate=Shell.Shift!.BusinessDate;var summaryCashier=Shell.Shift.CashierId;
            Shell.S.Cash.CloseShift(Shell.User!.Id, Shell.Shift!.Id, _countedValue, string.IsNullOrWhiteSpace(_reason) ? null : _reason.Trim());
            Shell.CloseDialog();
            Shell.Refresh();
            Shell.Toast(l["Toast_ShiftClosed"]);
            BackupRunner.RunAfterShiftClose(Shell);
            Shell.ShowDialog(new DailySummaryViewModel(Shell,summaryDate,summaryCashier));
        }
        catch (PosException ex) { SetError(nameof(Reason), Shell.ErrorText(ex)); }
    }
}

// ===================================================================== recovery / approval
public sealed class RecoveryResetViewModel(ShellViewModel shell, string username) : DialogViewModel(shell)
{
    string _user = username, _code = "", _password = "", _confirm = "";
    string? _replacement;

    public override double Width => 440;
    public string Username { get => _user; set { if (Set(ref _user, value)) SetError(nameof(Username), null); } }
    public string Code { get => _code; set { if (Set(ref _code, value)) SetError(nameof(Code), null); } }
    public string NewPassword { get => _password; set { if (Set(ref _password, value)) SetError(nameof(NewPassword), null); } }
    public string Confirm { get => _confirm; set { if (Set(ref _confirm, value)) SetError(nameof(Confirm), null); } }
    public string? ReplacementCode { get => _replacement; private set { if (Set(ref _replacement, value)) { Raise(nameof(IsDone)); Raise(nameof(IsForm)); } } }
    public bool IsDone => _replacement is not null;
    public bool IsForm => _replacement is null;
    public ICommand ResetCommand => new RelayCommand(Reset);

    void Reset()
    {
        var l = Localizer.Instance;
        SetError(nameof(Username), string.IsNullOrWhiteSpace(_user) ? l["Err_Required"] : null);
        SetError(nameof(Code), string.IsNullOrWhiteSpace(_code) ? l["Err_Required"] : null);
        SetError(nameof(NewPassword), _password.Length < UserService.MinPasswordLength ? string.Format(l["Err_WeakPassword"], UserService.MinPasswordLength) : null);
        SetError(nameof(Confirm), _confirm != _password ? l["Err_PasswordMismatch"] : null);
        if (HasErrors) return;
        var (ok, replacement) = Shell.S.Users.ResetWithRecoveryCode(_user.Trim(), _code, _password);
        if (!ok) { SetError(nameof(Code), l["Err_BadRecoveryCode"]); return; }
        ReplacementCode = replacement;
        Shell.Login.Username = _user.Trim();
    }
}

/// <summary>Owner approval for exactly one displayed action. The owner signs in here; the cashier's session is unchanged.</summary>
public sealed class ApprovalViewModel(ShellViewModel shell, string actionText, Action<long> approved) : DialogViewModel(shell)
{
    string _user = "", _password = "";

    public override double Width => 420;
    public string ActionText { get; } = actionText;
    public string Username { get => _user; set { if (Set(ref _user, value)) SetError(nameof(Password), null); } }
    public string Password { get => _password; set { if (Set(ref _password, value)) SetError(nameof(Password), null); } }
    public ICommand ApproveCommand => new RelayCommand(Approve);

    void Approve()
    {
        var l = Localizer.Instance;
        var result = Shell.S.Users.Login(_user.Trim(), _password);
        Password = "";
        if (result.Failure == AuthFailure.Locked) { SetError(nameof(Password), string.Format(l["Login_Locked"], result.LockedUntil?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))); return; }
        if (result.User is not { Role: Role.Owner } owner) { SetError(nameof(Password), l["Err_ApprovalDenied"]); return; }
        Shell.CloseDialog();
        approved(owner.Id);
    }
}
