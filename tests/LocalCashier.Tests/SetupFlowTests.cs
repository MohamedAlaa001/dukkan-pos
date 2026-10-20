using Dukkan.UI;
using LocalCashier.Persistence;
using Xunit;

namespace LocalCashier.Tests;

public class SetupFlowTests
{
    [Fact]
    public void First_run_setup_registers_the_owner_and_moves_to_recovery_codes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Sta.Run(() =>
            {
                var shell = new ShellViewModel(AppServices.Create(dir, null));
                var s = shell.Setup;
                s.StoreName = "Nour Market"; s.BusinessType = "grocery"; s.OwnerName = "Mohamed"; s.Username = "owner";
                s.Password = "Strong-pass-1"; s.ConfirmPassword = "Strong-pass-1"; s.TaxChoice = "Included";
                s.CreateCommand.Execute(null);
                Assert.False(s.HasErrors);
                Assert.True(s.IsCodesStep);
                Assert.NotEmpty(s.Codes);
                Assert.Equal("Nour Market", shell.S.Settings.GetValue("store_name"));
                Assert.Empty(shell.S.Config.Categories(true));          // a new shop starts with no predefined categories
                return 0;
            });
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }
}

public class PasswordBoxBindingTests
{
    sealed class Holder : System.ComponentModel.INotifyPropertyChanged
    {
        string _v = "";
        public string Value { get => _v; set { _v = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    [Fact]
    public void Typing_into_a_bound_password_box_reaches_the_view_model_from_the_very_first_keystroke()
    {
        // Regression: the attached Password property only listened after its value first changed, so a box bound to "" never reported typing.
        var typed = Sta.Run(() =>
        {
            var h = new Holder();
            var pb = new System.Windows.Controls.PasswordBox();
            System.Windows.Data.BindingOperations.SetBinding(pb, Ui.PasswordProperty,
                new System.Windows.Data.Binding(nameof(Holder.Value)) { Source = h, Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
            pb.Password = "secret-123";
            return h.Value;
        });
        Assert.Equal("secret-123", typed);
    }
}
