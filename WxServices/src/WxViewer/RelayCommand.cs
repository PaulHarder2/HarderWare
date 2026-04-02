using System.Windows.Input;

namespace WxViewer;

/// <summary>
/// Minimal ICommand implementation that delegates to Action/Func delegates.
/// CanExecuteChanged is wired to CommandManager.RequerySuggested so WPF
/// re-queries CanExecute automatically on UI interactions.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>Initialises a command with a parameterised execute delegate and optional canExecute predicate.</summary>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute    = execute;
        _canExecute = canExecute;
    }

    /// <summary>Convenience constructor for parameterless actions.</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter)    => _execute(parameter);
}
