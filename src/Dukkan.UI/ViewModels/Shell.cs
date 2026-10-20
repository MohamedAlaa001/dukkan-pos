using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;

namespace Dukkan.UI;

/// <summary>Indexable error texts for the views ("{Binding Errs[Name]}"); empty string means no error.</summary>
public sealed class ErrorBag : INotifyPropertyChanged
{
    readonly Dictionary<string, string> _d = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _d.TryGetValue(key, out var v) ? v : "";

    public void Set(string key, string? message)
    {
        if (string.IsNullOrEmpty(message)) _d.Remove(key); else _d[key] = message;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public bool Any => _d.Count > 0;

    public void Clear() { _d.Clear(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]")); }
}

/// <summary>Validation state: INotifyDataErrorInfo drives the red input border, ErrorBag drives the message text.</summary>
public abstract class ValidatableViewModel : ObservableObject, INotifyDataErrorInfo
{
    readonly Dictionary<string, string> _errors = [];

    public ErrorBag Errs { get; } = new();

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Count > 0;

    public IEnumerable GetErrors(string? propertyName) =>
        propertyName is not null && _errors.TryGetValue(propertyName, out var m) ? new[] { m } : Array.Empty<string>();

    protected void SetError(string property, string? message)
    {
        var had = _errors.ContainsKey(property);
        if (string.IsNullOrEmpty(message)) _errors.Remove(property); else _errors[property] = message;
        Errs.Set(property, message);
        if (had || !string.IsNullOrEmpty(message)) ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(property));
        Raise(nameof(HasErrors));
    }

    protected void ClearErrors()
    {
        foreach (var k in _errors.Keys.ToList()) SetError(k, null);
    }
}

public abstract class DialogViewModel(ShellViewModel shell) : ValidatableViewModel
{
    public ShellViewModel Shell { get; } = shell;
    public virtual double Width => 460;
    public ICommand CloseCommand => new RelayCommand(Shell.CloseDialog);
}

/// <summary>Generic confirmation (delete, clear cart…). Presentation only — it just runs the supplied callback.</summary>
public sealed class ConfirmViewModel(ShellViewModel shell, string title, string message, string confirmText, bool danger, Action onConfirm) : DialogViewModel(shell)
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;
    public bool IsDanger { get; } = danger;
    public override double Width => 420;
    public ICommand ConfirmCommand => new RelayCommand(() => { Shell.CloseDialog(); onConfirm(); });
}

