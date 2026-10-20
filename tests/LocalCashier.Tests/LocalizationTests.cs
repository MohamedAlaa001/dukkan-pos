using System.Text.RegularExpressions;
using Dukkan.UI;
using Xunit;

namespace LocalCashier.Tests;

public class LocalizationTests
{
    static bool IsBuildOutput(string p) => p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar);

    static string UiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Dukkan.UI"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Dukkan.UI");
    }

    [Fact]
    public void Every_literal_key_used_in_screens_exists_in_English_and_Arabic()
    {
        var keys = new HashSet<string>();
        var xaml = new Regex(@"\{ui:T\s+([A-Za-z0-9_]+)\}");
        var code = new Regex(@"\b(?:L|l|Localizer\.Instance)\[""([A-Za-z0-9_]+)""\]");
        foreach (var f in Directory.EnumerateFiles(UiRoot(), "*.xaml", SearchOption.AllDirectories).Where(p => !IsBuildOutput(p)))
            foreach (Match m in xaml.Matches(File.ReadAllText(f))) keys.Add(m.Groups[1].Value);
        foreach (var f in Directory.EnumerateFiles(UiRoot(), "*.cs", SearchOption.AllDirectories).Where(p => !IsBuildOutput(p)))
            foreach (Match m in code.Matches(File.ReadAllText(f))) keys.Add(m.Groups[1].Value);

        var missing = keys.Where(k => Strings.Get(k, false) == k || Strings.Get(k, true) == k).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Missing translations: " + string.Join(", ", missing));
    }

    [Fact]
    public void Arabic_text_is_really_Arabic_for_the_new_screens()
    {
        foreach (var k in new[] { "Nav_Suppliers", "Sup_NewPurchase", "Sup_PayNow", "Sup_TabStatement", "Credit_Plain", "Credit_Installments", "Col_DueDate", "Col_Paid", "Col_Remaining" })
            Assert.Matches("[؀-ۿ]", Strings.Get(k, true));
    }
}
