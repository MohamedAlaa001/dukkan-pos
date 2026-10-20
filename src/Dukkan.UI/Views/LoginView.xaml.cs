using System.Windows;
using System.Windows.Controls;

namespace Dukkan.UI.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        Loaded += (_, _) => UserBox.Focus();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) UserBox.Focus(); };
    }

    void Eye_Click(object sender, RoutedEventArgs e)
    {
        // The command has already toggled visibility; move focus to whichever box is now shown.
        Dispatcher.BeginInvoke(() =>
        {
            if (PwdText.IsVisible) { PwdText.Focus(); PwdText.CaretIndex = PwdText.Text.Length; }
            else PwdBox.Focus();
        });
    }

    void Lang_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow?.DataContext is ShellViewModel shell) shell.ToggleLanguageCommand.Execute(null);
    }
}
