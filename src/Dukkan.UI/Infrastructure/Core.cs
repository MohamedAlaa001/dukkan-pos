using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Dukkan.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>Icon glyphs from one family (Segoe Fluent Icons, falling back to Segoe MDL2 Assets).</summary>
public static class Icons
{
    public const string Home = "";
    public const string Cart = "";
    public const string Bag = "";
    public const string Tag = "";
    public const string Package = "";
    public const string People = "";
    public const string Person = "";
    public const string Calculator = "";
    public const string Chart = "";
    public const string Settings = "";
    public const string Search = "";
    public const string Bell = "";
    public const string Lock = "";
    public const string Eye = "";
    public const string EyeOff = "";
    public const string Calendar = "";
    public const string ChevronDown = "";
    public const string ChevronUp = "";
    public const string ChevronLeft = "";
    public const string ChevronRight = "";
    public const string Close = "";
    public const string Add = "";
    public const string Remove = "";
    public const string Delete = "";
    public const string Edit = "";
    public const string Save = "";
    public const string Phone = "";
    public const string More = "";
    public const string Back = "";
    public const string Forward = "";
    public const string Check = "";
    public const string Money = "";
    public const string Card = "";
    public const string Transfer = "";
    public const string Credit = "";
    public const string Document = "";
    public const string Filter = "";
    public const string Print = "";
    public const string Download = "";
    public const string Info = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Photo = "";
    public const string ArrowUp = "";
    public const string ArrowDown = "";
    public const string History = "";
    public const string Pause = "";
    public const string SignOut = "";
    public const string Globe = "";
    public const string Mail = "";
    public const string Shop = "";
}
