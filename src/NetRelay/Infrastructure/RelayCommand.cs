using System.Windows.Input;

namespace NetRelay.Infrastructure;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (canExecute == null) return true;
        if (parameter == null) return !typeof(T).IsValueType;
        return canExecute((T)parameter);
    }

    public void Execute(object? parameter)
    {
        if (parameter != null || !typeof(T).IsValueType)
        {
            execute((T)parameter!);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
