using System.IO;
using System.Windows;

namespace Dukkan.UI;

public partial class App : System.Windows.Application
{
    /// <summary>The composition root, created once at startup (before the first window).</summary>
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
#if DEBUG
        string? Opt(string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
#endif
        try
        {
#if DEBUG
            // Developer/test switches only; the shipped (Release) build always uses the shop's own data folder and real printers.
            Services = AppServices.Create(Opt("--data"), args.Contains("--silent-print") ? new SilentPrinter() : null);
            if (args.Contains("--demo") && Services.NeedsSetup) DemoSeeder.Seed(Services);
            if (Opt("--binding-log") is { } bindingLog)
            {
                File.AppendAllText(bindingLog, "");
                System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(bindingLog));
                System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
                System.Diagnostics.Trace.AutoFlush = true;
            }
#else
            Services = AppServices.Create(null, null);
#endif
        }
        catch (Exception ex) when (ex is InvalidOperationException or LocalCashier.Domain.PosException or IOException)
        {
            MessageBox.Show(ex.Message, "Dukkan", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        new MainWindow().Show();
    }
}

#if DEBUG
/// <summary>Accepts print jobs without a physical printer (design review and automated checks only).</summary>
sealed class SilentPrinter : LocalCashier.Printing.IDocumentPrinter
{
    public IReadOnlyList<string> Printers() => ["HPRT LPQ80", "A4 Office Printer"];

    public void Print(string printer, IReadOnlyList<FrameworkElement> pages, string jobName) { }
}

#endif
