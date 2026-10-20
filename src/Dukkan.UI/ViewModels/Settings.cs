using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using LocalCashier.Application;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace Dukkan.UI;

public sealed class UserItem(UserRow r) : LocalizedObject
{
    public UserRow Row { get; } = r;
    public long Id => Row.Id;
    public string Username => Row.Username;
    public string DisplayName => Row.DisplayName;
    public string RoleText => Localizer.Instance[Row.Role == Role.Owner ? "Role_Owner" : "Role_Cashier"];
    public string StatusText => Localizer.Instance[Row.Locked ? "User_Locked" : Row.Active ? "Status_Active" : "Status_Inactive"];
    public BadgeKind StatusKind => Row.Locked ? BadgeKind.Danger : Row.Active ? BadgeKind.Success : BadgeKind.Neutral;
    public bool Active => Row.Active;
}

public sealed class AuditItem(AuditRow r) : LocalizedObject
{
    public string When { get; } = DateTimeOffset.Parse(r.Timestamp, CultureInfo.InvariantCulture).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
    public string Actor { get; } = r.Actor ?? "—";
    public string Approver { get; } = r.Approver ?? "";
    public string Action { get; } = r.Action;
    public string Target { get; } = r.Target ?? "";
    public string Reason { get; } = r.Reason ?? "";
}

public sealed class SettingsViewModel : ValidatableViewModel
{
    readonly ShellViewModel _shell;
    readonly AppServices _s;
    readonly Action<bool> _setStartup, _setFullscreen;
    string _store = "", _phone = "", _address = "", _footer = "", _taxPercent = "0";
    bool _autoPrint;
    bool _receiptArabic, _startFull, _startWithWindows;
    string? _recPrinter, _a4Printer, _labelPrinter;
    string _secret1 = "", _secret2 = "", _externalDir = "";
    string _backupStatus = "";
    string _restorePath = "", _restoreSecret = "";
    string _auditFilter = "";
    string _printMessage = "";
    BadgeKind _printKind = BadgeKind.Info;
    int _tab;

    public SettingsViewModel(ShellViewModel shell, Action<bool>? setStartup=null, Action<bool>? setFullscreen=null)
    {
        _shell = shell;
        _s = shell.S;
        _setStartup=setStartup??StartupRegistration.Set;
        _setFullscreen=setFullscreen??(value=>(System.Windows.Application.Current?.MainWindow as MainWindow)?.SetFullScreen(value));
        var st = _s.Settings;
        Catalog = new CatalogSettingsViewModel(shell);
        Payments=new PaymentSettingsViewModel(shell);
        Promotions=new PromotionWorkspaceViewModel(shell);
        _store = st.GetValue("store_name") ?? "";
        _phone = st.GetValue("store_phone") ?? "";
        _address = st.GetValue("store_address") ?? "";
        _footer = st.GetValue("receipt_footer") ?? "";
        _taxPercent = st.GetValue("default_tax_percent") ?? "0";
        _autoPrint = st.GetValue("auto_print_receipt") == "1";
        _receiptArabic = st.GetValue("receipt_language") == "ar";
        _startFull = st.GetValue("start_fullscreen") == "1";
        _startWithWindows = StartupRegistration.IsEnabled;
        _recPrinter = st.GetValue("printer_receipt");
        _a4Printer = st.GetValue("printer_a4");
        _labelPrinter = st.GetValue("printer_label");
        _externalDir = st.GetValue("backup_external_dir") ?? "";
        Printers = [];
        Users = [];
        Audit = [];
        SaveStoreCommand = new RelayCommand(SaveStore);
        SavePrintingCommand = new RelayCommand(SavePrinting);
        RefreshPrintersCommand = new RelayCommand(LoadPrinters);
        TestReceiptCommand = new RelayCommand(TestReceipt);
        TestLabelCommand = new RelayCommand(TestLabel);
        AddUserCommand = new RelayCommand(() => _shell.ShowDialog(new UserEditViewModel(_shell, LoadUsers)));
        ToggleUserCommand = new RelayCommand(p => ToggleUser((UserItem)p!));
        ResetPasswordCommand = new RelayCommand(p => _shell.ShowDialog(new ResetPasswordViewModel(_shell, (UserItem)p!)));
        ChangeMyPasswordCommand = new RelayCommand(() => _shell.ShowDialog(new ChangePasswordViewModel(_shell)));
        NewRecoveryCodesCommand = new RelayCommand(() => _shell.Confirm(L["Rec_NewTitle"], L["Rec_NewNote"], L["Rec_Generate"], false, () =>
            _shell.ShowDialog(new RecoveryCodesViewModel(_shell, _s.Users.GenerateRecoveryCodes(_shell.User!.Id)))));
        SaveSecretCommand = new RelayCommand(SaveSecret);
        SaveDirCommand = new RelayCommand(SaveDir);
        BackupNowCommand = new RelayCommand(BackupNow);
        RestoreCommand = new RelayCommand(Restore);
        ReloadAuditCommand = new RelayCommand(LoadAudit);
        LoadPrinters();
        LoadUsers();
        LoadAudit();
        RefreshBackupStatus();
    }

    public Localizer L => Localizer.Instance;
    public PaymentSettingsViewModel Payments {get;}
    public PromotionWorkspaceViewModel Promotions {get;}
    public ObservableCollection<string> Printers { get; }
    public ObservableCollection<UserItem> Users { get; }
    public ObservableCollection<AuditItem> Audit { get; }
    public ICommand SaveStoreCommand { get; }
    public ICommand SavePrintingCommand { get; }
    public ICommand RefreshPrintersCommand { get; }
    public ICommand TestReceiptCommand { get; }
    public ICommand TestLabelCommand { get; }
    public ICommand AddUserCommand { get; }
    public ICommand ToggleUserCommand { get; }
    public ICommand ResetPasswordCommand { get; }
    public ICommand ChangeMyPasswordCommand { get; }
    public ICommand NewRecoveryCodesCommand { get; }
    public ICommand SaveSecretCommand { get; }
    public ICommand SaveDirCommand { get; }
    public ICommand BackupNowCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ReloadAuditCommand { get; }

    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public CatalogSettingsViewModel Catalog { get; private set; } = null!;
    public string StoreName { get => _store; set { if (Set(ref _store, value)) SetError(nameof(StoreName), null); } }
    public string StorePhone { get => _phone; set => Set(ref _phone, value); }
    public string StoreAddress { get => _address; set => Set(ref _address, value); }
    public string ReceiptFooter { get => _footer; set => Set(ref _footer, value); }
    public string TaxPercent { get => _taxPercent; set { if (Set(ref _taxPercent, value)) SetError(nameof(TaxPercent), null); } }
    public bool AutoPrint { get => _autoPrint; set => Set(ref _autoPrint, value); }
    public bool StartFullScreen { get => _startFull; set => Set(ref _startFull, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool ReceiptArabic { get => _receiptArabic; set { if (Set(ref _receiptArabic, value)) Raise(nameof(ReceiptEnglish)); } }
    public bool ReceiptEnglish { get => !_receiptArabic; set => ReceiptArabic = !value; }
    public string TaxModeText => L[_s.Settings.PricesIncludeTax() == true ? "Setup_TaxIncluded" : "Setup_TaxExcluded"];
    public string? ReceiptPrinter { get => _recPrinter; set => Set(ref _recPrinter, value); }
    public string? A4Printer { get => _a4Printer; set => Set(ref _a4Printer, value); }
    public string? LabelPrinter { get => _labelPrinter; set => Set(ref _labelPrinter, value); }
    public string PrintMessage { get => _printMessage; private set { Set(ref _printMessage, value); Raise(nameof(HasPrintMessage)); } }
    public bool HasPrintMessage => _printMessage.Length > 0;
    public BadgeKind PrintKind { get => _printKind; private set => Set(ref _printKind, value); }
    public bool IsArabic { get => Localizer.Instance.IsArabic; set { if (value != Localizer.Instance.IsArabic) _shell.SetArabic(value); Raise(); Raise(nameof(IsEnglish)); } }
    public bool IsEnglish { get => !IsArabic; set => IsArabic = !value; }

    // ---- backup
    public string Secret1 { get => _secret1; set { if (Set(ref _secret1, value)) SetError(nameof(Secret1), null); } }
    public string Secret2 { get => _secret2; set { if (Set(ref _secret2, value)) SetError(nameof(Secret1), null); } }
    public string ExternalDir { get => _externalDir; set => Set(ref _externalDir, value); }
    public string BackupStatus { get => _backupStatus; private set => Set(ref _backupStatus, value); }
    public string RestorePath { get => _restorePath; set { if (Set(ref _restorePath, value)) SetError(nameof(RestorePath), null); } }
    public string RestoreSecret { get => _restoreSecret; set { if (Set(ref _restoreSecret, value)) SetError(nameof(RestorePath), null); } }
    public string AuditFilter { get => _auditFilter; set => Set(ref _auditFilter, value); }

    // ---- about
    public DiagnosticsInfo Info => _s.Queries.Diagnostics() with { AppVersion = ProductVersion.Current };
    public string DatabaseSize => $"{Info.DatabaseBytes / 1048576d:0.0} MB";
    public string FreeDisk => $"{Info.FreeDiskBytes / 1073741824d:0.0} GB ({Info.DriveName})";
    public bool LowDisk => Info.FreeDiskBytes < 1_073_741_824;

    void SaveStore()
    {
        SetError(nameof(StoreName), string.IsNullOrWhiteSpace(_store) ? L["Err_Required"] : null);
        SetError(nameof(TaxPercent), decimal.TryParse(_taxPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 100 ? null : L["Err_Number"]);
        if (HasErrors) return;
        var owner = _shell.User!.Id;
        var st = _s.Settings;
        st.SetValue(owner, "store_name", _store.Trim());
        Localizer.Instance.StoreName = _store.Trim();
        st.SetValue(owner, "store_phone", _phone.Trim());
        st.SetValue(owner, "store_address", _address.Trim());
        st.SetValue(owner, "receipt_footer", _footer.Trim());
        st.SetValue(owner, "default_tax_percent", _taxPercent.Trim());
        st.SetValue(owner, "auto_print_receipt", _autoPrint ? "1" : "0");
        st.SetValue(owner, "receipt_language", _receiptArabic ? "ar" : "en");
        st.SetValue(owner, "start_fullscreen", _startFull ? "1" : "0");
        _setStartup(_startWithWindows);
        _setFullscreen(_startFull);
        _shell.Toast(L["Toast_SettingsSaved"]);
    }

    // ---- printing
    void LoadPrinters()
    {
        // Clearing a bound ComboBox source resets its two-way SelectedItem.
        // Preserve the user's choices, and enumerate first so a driver failure
        // cannot discard the existing list or configuration.
        var receipt = _recPrinter;
        var a4 = _a4Printer;
        var label = _labelPrinter;
        try
        {
            var available = _s.Printer.Printers().ToArray();
            Printers.Clear();
            foreach (var p in available) Printers.Add(p);
            ReceiptPrinter = receipt;
            A4Printer = a4;
            LabelPrinter = label;
        }
        catch (Exception ex) { PrintMessage = ex.Message; PrintKind = BadgeKind.Danger; }
    }

    void SavePrinting()
    {
        var owner = _shell.User!.Id;
        _s.Settings.SetValue(owner, "printer_receipt", _recPrinter ?? "");
        _s.Settings.SetValue(owner, "printer_a4", _a4Printer ?? "");
        _s.Settings.SetValue(owner, "printer_label", _labelPrinter ?? "");
        _shell.Toast(L["Toast_SettingsSaved"]);
    }

    StoreInfo StoreInfoPreview() => new(string.IsNullOrWhiteSpace(_store) ? Localizer.Instance["Brand_Name"] : _store, _phone, _address, _footer, _receiptArabic);

    void TestReceipt()
    {
        if (string.IsNullOrWhiteSpace(_recPrinter)) { PrintMessage = L["Print_NoPrinter"]; PrintKind = BadgeKind.Warning; return; }
        var sample = new ReceiptModel("TEST-000001", _s.Clock.Now.ToString("o"), _shell.User!.DisplayName,
            [new ReceiptLine("T-Shirt", 2, 250m, 0m, 500m, "L", "Black"), new ReceiptLine("قميص بولو", 1, 300m, 0m, 300m, "M", "كريمي")],
            0m, 0m, 800m, [new ReceiptPayment("cash", 800m, 1000m)], 200m, 0m);
        try
        {
            _s.Printer.Print(_recPrinter!, [DocumentBuilder.Receipt80(sample, StoreInfoPreview(), false)], "Dukkan test");
            PrintMessage = L["Print_TestSent"]; PrintKind = BadgeKind.Success;
        }
        catch (Exception ex) { PrintMessage = string.Format(L["Print_Failed"], ex.Message); PrintKind = BadgeKind.Danger; }
    }

    void TestLabel()
    {
        var item = new LabelItem("T-Shirt", "L", "Black", 250m, Ean13.FromSequence(1), "TS001-BLK-L", "EGP");
        var name = string.IsNullOrWhiteSpace(_labelPrinter) ? _recPrinter : _labelPrinter;
        if (string.IsNullOrWhiteSpace(name)) { PrintMessage = L["Print_NoPrinter"]; PrintKind = BadgeKind.Warning; return; }
        try
        {
            _s.Printer.Print(name!, [DocumentBuilder.Label(item, 50, 30)], "Dukkan label test");
            PrintMessage = L["Print_TestSent"]; PrintKind = BadgeKind.Success;
        }
        catch (Exception ex) { PrintMessage = string.Format(L["Print_Failed"], ex.Message); PrintKind = BadgeKind.Danger; }
    }

    // ---- users
    void LoadUsers()
    {
        Users.Clear();
        foreach (var u in _s.Users.List()) Users.Add(new UserItem(u));
    }

    void ToggleUser(UserItem u)
    {
        try { _s.Users.SetActive(_shell.User!.Id, u.Id, !u.Active); LoadUsers(); _shell.Toast(L["Toast_Saved"]); }
        catch (PosException ex) { _shell.ToastError(ex); }
    }

    // ---- audit
    void LoadAudit()
    {
        Audit.Clear();
        foreach (var a in _s.Audit.List(string.IsNullOrWhiteSpace(_auditFilter) ? null : _auditFilter.Trim(), null, null, 300)) Audit.Add(new AuditItem(a));
    }

    // ---- backup
    void RefreshBackupStatus()
    {
        var st = _s.Backup.Status();
        string Fmt(DateTimeOffset? t) => t is null ? L["bk_never"] : t.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        BackupStatus = string.Format(L["bk_status"], Fmt(st.LastLocal), Fmt(st.LastExternal)) + (st.ExternalFailure ?? st.LocalFailure) switch { { } f => "\n" + f, _ => "" };
        _shell.RefreshNotifications();
    }

    void SaveSecret()
    {
        if (_secret1.Length < 10 || _secret1 != _secret2) { SetError(nameof(Secret1), L["Err_Secret"]); return; }
        _s.Secrets.Save(_secret1);
        Secret1 = ""; Secret2 = "";
        RefreshBackupStatus();
        _shell.Toast(L["Toast_Saved"]);
    }

    void SaveDir()
    {
        _s.Settings.SetValue(_shell.User!.Id, "backup_external_dir", _externalDir.Trim());
        RefreshBackupStatus();
        _shell.Toast(L["Toast_Saved"]);
    }

    void BackupNow()
    {
        if (!_s.Secrets.Exists) { _shell.Toast(L["Notif_BackupNoSecret"], BadgeKind.Warning); return; }
        BackupRunner.Run(_shell, external: true);
        RefreshBackupStatus();
        var st = _s.Backup.Status();
        if (st.LocalFailure is null && st.ExternalFailure is null) _shell.Toast(L["bk_done"]);
        else _shell.Toast(st.ExternalFailure ?? st.LocalFailure ?? "", BadgeKind.Danger);
    }

    /// <summary>Restore replaces the live database: owner re-authentication first, then the app closes.</summary>
    void Restore()
    {
        SetError(nameof(RestorePath), string.IsNullOrWhiteSpace(_restorePath) || _restoreSecret.Length == 0 ? L["Err_Required"] : null);
        if (HasErrors) return;
        _shell.ShowDialog(new ApprovalViewModel(_shell, L["bk_restore_title"], owner =>
        {
            try
            {
                var res = RestoreService.Restore(_restorePath.Trim().Trim('"'), _restoreSecret, _s.Db.Path, Path.Combine(_s.DataDir, "preserved"), _s.Clock, owner);
                _shell.ShowDialog(new RestoreDoneViewModel(_shell, res));
            }
            catch (PosException ex)
            {
                _shell.Toast(_shell.ErrorText(ex), BadgeKind.Danger);
            }
        }));
    }
}

public sealed class RestoreDoneViewModel(ShellViewModel shell, RestoreResult result) : DialogViewModel(shell)
{
    public override double Width => 520;
    public RestoreResult Result { get; } = result;
    public string Summary => string.Format(Localizer.Instance["bk_restore_done"], Result.BackupCreatedAt, Result.SalesInBackup, Result.LastSaleNumber ?? "-", Result.PreservedCopy);
    public ICommand CloseAppCommand => new RelayCommand(() => System.Windows.Application.Current.Shutdown());
}

public sealed class UserEditViewModel : DialogViewModel
{
    readonly Action _saved;
    string _username = "", _display = "", _password = "", _confirm = "";
    bool _owner;

    public UserEditViewModel(ShellViewModel shell, Action saved) : base(shell) => _saved = saved;

    public override double Width => 460;
    public string Username { get => _username; set { if (Set(ref _username, value)) SetError(nameof(Username), null); } }
    public string DisplayName { get => _display; set { if (Set(ref _display, value)) SetError(nameof(DisplayName), null); } }
    public string Password { get => _password; set { if (Set(ref _password, value)) SetError(nameof(Password), null); } }
    public string Confirm { get => _confirm; set { if (Set(ref _confirm, value)) SetError(nameof(Confirm), null); } }
    public bool IsOwnerRole { get => _owner; set => Set(ref _owner, value); }
    public ICommand SaveCommand => new RelayCommand(Save);

    void Save()
    {
        var l = Localizer.Instance;
        SetError(nameof(Username), string.IsNullOrWhiteSpace(_username) ? l["Err_Required"] : null);
        SetError(nameof(DisplayName), string.IsNullOrWhiteSpace(_display) ? l["Err_Required"] : null);
        SetError(nameof(Password), _password.Length < UserService.MinPasswordLength ? string.Format(l["Err_WeakPassword"], UserService.MinPasswordLength) : null);
        SetError(nameof(Confirm), _confirm != _password ? l["Err_PasswordMismatch"] : null);
        if (HasErrors) return;
        try
        {
            Shell.S.Users.CreateByOwner(Shell.User!.Id, _username.Trim(), _display.Trim(), _owner ? Role.Owner : Role.Cashier, _password);
            Shell.CloseDialog();
            _saved();
            Shell.Toast(l["Toast_Saved"]);
        }
        catch (PosException ex) { SetError(nameof(Username), Shell.ErrorText(ex)); }
    }
}

public sealed class ResetPasswordViewModel(ShellViewModel shell, UserItem user) : DialogViewModel(shell)
{
    string _password = "", _confirm = "";

    public override double Width => 440;
    public UserItem User { get; } = user;
    public string Password { get => _password; set { if (Set(ref _password, value)) SetError(nameof(Password), null); } }
    public string Confirm { get => _confirm; set { if (Set(ref _confirm, value)) SetError(nameof(Confirm), null); } }
    public ICommand SaveCommand => new RelayCommand(Save);

    void Save()
    {
        var l = Localizer.Instance;
        SetError(nameof(Password), _password.Length < UserService.MinPasswordLength ? string.Format(l["Err_WeakPassword"], UserService.MinPasswordLength) : null);
        SetError(nameof(Confirm), _confirm != _password ? l["Err_PasswordMismatch"] : null);
        if (HasErrors) return;
        try
        {
            Shell.S.Users.ResetPassword(Shell.User!.Id, User.Id, _password);
            Shell.CloseDialog();
            Shell.Toast(l["Toast_Saved"]);
        }
        catch (PosException ex) { SetError(nameof(Password), Shell.ErrorText(ex)); }
    }
}

public sealed class ChangePasswordViewModel(ShellViewModel shell) : DialogViewModel(shell)
{
    string _current = "", _password = "", _confirm = "";

    public override double Width => 440;
    public string Current { get => _current; set { if (Set(ref _current, value)) SetError(nameof(Current), null); } }
    public string Password { get => _password; set { if (Set(ref _password, value)) SetError(nameof(Password), null); } }
    public string Confirm { get => _confirm; set { if (Set(ref _confirm, value)) SetError(nameof(Confirm), null); } }
    public ICommand SaveCommand => new RelayCommand(Save);

    void Save()
    {
        var l = Localizer.Instance;
        SetError(nameof(Password), _password.Length < UserService.MinPasswordLength ? string.Format(l["Err_WeakPassword"], UserService.MinPasswordLength) : null);
        SetError(nameof(Confirm), _confirm != _password ? l["Err_PasswordMismatch"] : null);
        if (HasErrors) return;
        try
        {
            Shell.S.Users.ChangePassword(Shell.User!.Id, _current, _password);
            Shell.CloseDialog();
            Shell.Toast(l["Toast_Saved"]);
        }
        catch (PosException ex) { SetError(nameof(Current), Shell.ErrorText(ex)); }
    }
}

public sealed class RecoveryCodesViewModel(ShellViewModel shell, IReadOnlyList<string> codes) : DialogViewModel(shell)
{
    public override double Width => 480;
    public IReadOnlyList<string> Codes { get; } = codes;
    public ICommand CopyCommand => new RelayCommand(() =>
    {
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Codes)); Shell.Toast(Localizer.Instance["Toast_Copied"], BadgeKind.Info); }
        catch (System.Runtime.InteropServices.COMException) { Shell.Toast(Localizer.Instance["Toast_CopyFailed"], BadgeKind.Warning); }
    });
}
