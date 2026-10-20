using System.IO;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using LocalCashier.Printing;

namespace Dukkan.UI;

/// <summary>Clock whose time can be moved while demo history is generated; it follows the system clock otherwise.</summary>
public sealed class SettableClock : IClock
{
    DateTimeOffset? _fixed;

    public DateTimeOffset Now => _fixed ?? DateTimeOffset.Now;

    public void Set(DateTimeOffset? value) => _fixed = value;
}

/// <summary>Composition root: one place that builds the database and every service the screens use.</summary>
public sealed class AppServices
{
    public required string DataDir { get; init; }
    public required SettableClock Clock { get; init; }
    public required PosDatabase Db { get; init; }
    public required UserService Users { get; init; }
    public required SettingsService Settings { get; init; }
    public required CatalogService Catalog { get; init; }
    public required StyleCatalogService Styles { get; init; }
    public required SupplierService Suppliers { get; init; }
    public required ImportService Imports { get; init; }
    public required CatalogConfigService Config { get; init; }
    public required CustomerCreditService Credit { get; init; }
    public required InstallmentService Installments { get; init; }
    public required CustomerService Customers { get; init; }
    public required ShiftService Shifts { get; init; }
    public required InventoryService Inventory { get; init; }
    public required StockAdjustmentService Adjustments { get; init; }
    public StockControlService Stock => new(Db, Clock);
    public PaymentMethodService PaymentMethods => new(Db,Clock);
    public required SaleService Sales { get; init; }
    public required ReturnService Returns { get; init; }
    public required CollectionService Collections { get; init; }
    public required CashService Cash { get; init; }
    public required ReportService Reports { get; init; }
    public required QueryService Queries { get; init; }
    public required HeldCartService Held { get; init; }
    public required AuditService Audit { get; init; }
    public required PrintJobService PrintJobs { get; init; }
    public required PrintOrchestrator Printing { get; init; }
    public required IDocumentPrinter Printer { get; init; }
    public required BackupService Backup { get; init; }
    public required BackupSecretStore Secrets { get; init; }

    public string LocalBackupDir => Path.Combine(DataDir, "backups");

    /// <summary>%LocalAppData%\Dukkan. A folder left by the earlier product name (Kaboky) is moved there once, data intact.</summary>
    public static string DefaultDataDir()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(root, "Dukkan");
        var old = Path.Combine(root, "Kaboky");
        if (!Directory.Exists(dir) && Directory.Exists(old))
        {
            try { Directory.Move(old, dir); }
            catch (IOException) { return old; }              // in use or locked: keep working from the old folder rather than risk the data
            catch (UnauthorizedAccessException) { return old; }
        }
        return dir;
    }

    /// <param name="dataDir">Folder holding pos.db and backups. Defaults to <see cref="DefaultDataDir"/>.</param>
    /// <param name="printer">Replace the Windows printer (tests and design screenshots use a silent one).</param>
    public static AppServices Create(string? dataDir = null, IDocumentPrinter? printer = null)
    {
        var dir = dataDir ?? DefaultDataDir();
        Directory.CreateDirectory(dir);
        var clock = new SettableClock();
        var db = new PosDatabase(Path.Combine(dir, "pos.db"));
        var sales = new SaleService(db, clock);
        var settings = new SettingsService(db, clock);
        var jobs = new PrintJobService(db, clock);
        var winPrinter = printer ?? new WindowsPrinter();
        jobs.RecoverUncertain();   // anything left mid-submission by a crash is shown as "uncertain", never assumed printed
        var created = new AppServices
        {
            DataDir = dir, Clock = clock, Db = db,
            Users = new UserService(db, clock), Settings = settings, Catalog = new CatalogService(db, clock), Styles = new StyleCatalogService(db, clock),
            Customers = new CustomerService(db, clock), Shifts = new ShiftService(db, clock), Inventory = new InventoryService(db, clock),
            Adjustments = new StockAdjustmentService(db, clock), Sales = sales, Returns = new ReturnService(db, clock),
            Collections = new CollectionService(db, clock), Cash = new CashService(db, clock), Reports = new ReportService(db),
            Queries = new QueryService(db, clock), Held = new HeldCartService(db, clock), Audit = new AuditService(db), PrintJobs = jobs,
            Printing = new PrintOrchestrator(sales, jobs, settings, winPrinter), Printer = winPrinter,
            Backup = new BackupService(db, clock), Secrets = new BackupSecretStore(dir),
            Config = new CatalogConfigService(db, clock), Imports = new ImportService(db, clock, new StyleCatalogService(db, clock)), Suppliers = new SupplierService(db, clock), Credit = new CustomerCreditService(db, clock), Installments = new InstallmentService(db, clock),
        };
        ProductImages.Configure(created.Styles);
        return created;
    }

    public bool NeedsSetup => !Users.AnyUsers();
}
