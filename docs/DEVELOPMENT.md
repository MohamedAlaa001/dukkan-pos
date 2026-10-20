# Development

## Projects

- `src/Dukkan.UI`: WPF screens, view models, localization and application startup.
- `src/LocalCashier.Domain`: money, quantities, pricing, promotions and return calculations.
- `src/LocalCashier.Application`: service contracts and terminal abstraction.
- `src/LocalCashier.Persistence`: SQLite schema, transactions, ledgers, permissions and queries.
- `src/LocalCashier.Printing`: receipts, invoices, labels and operational documents.
- `tests/LocalCashier.Tests`: domain, persistence, migration, printing and UI regression checks.
- `installer`: self-contained publishing, install/uninstall and executable icon tooling.

Build and test on Windows with the .NET 8 SDK using `Dukkan.sln`. Publishing requires NuGet access on the build machine; the resulting package runs offline. Run `installer/Build-Installer.ps1` followed by `installer/Build-SetupExe.ps1` to include the user guide and create the standalone setup.

## UI and assets

`src/Dukkan.UI/Theme/Colors.xaml` is the shared navy/orange palette. Other theme dictionaries define controls, inputs and feedback. Keep numeric fields left-to-right in both interface languages and test layouts at 1366×768 and 1920×1080.

Runtime resources live in `src/Dukkan.UI/Assets`: category images, interface masks, logo, application icon and payment artwork. `GlyphIcon` tints interface masks with the current foreground color. These resources are required by the UI; design mockups and generation intermediates are not build inputs.

User-visible text belongs in the bilingual Strings tables. Keep domain rules and service permissions separate from UI state. Store money as integer piastres and quantities as milli-units. Posted history is immutable, posting retries must reuse operation IDs, and schema migrations must preserve verified snapshots and roll back atomically.

## Isolated UI checks

Debug builds accept isolated data, seeded demo and capture options. Never point these at a shop database. For example, after a Debug build, run from PowerShell:

```powershell
& '.\src\Dukkan.UI\bin\Debug\net8.0-windows\Dukkan.exe' `
  '--data=H:\Dukkan-test-data' '--demo' '--silent-print' `
  '--login=owner:owner-demo-1' '--page=products' '--lang=en' `
  '--size=1366x768' '--shot=H:\Dukkan-test-data\products.png' '--delay=1200'
```

Change the sample paths to a dedicated local test directory. `--lang=ar` checks Arabic. `--binding-log=<path>` captures WPF binding errors. Additional page/dialog/tab options are defined in `MainWindow.xaml.cs`. Demo login values apply only to newly seeded practice data and are not production credentials. Release builds ignore these switches and use the normal data folder and printers.

The application assembly remains `Dukkan`, preserving resource and installation identity. The few references to the earlier installation name in startup/installer code intentionally support existing data and shortcut migration.
