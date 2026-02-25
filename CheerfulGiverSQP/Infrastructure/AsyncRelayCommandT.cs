using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CheerfulGiverNXT.Infrastructure;

/// <summary>
/// AsyncRelayCommand with a CommandParameter.
///
/// = This intentionally mirrors the existing non-generic AsyncRelayCommand used by CheerfulGiverNXT.
/// = Keeping it in the same namespace lets view models use AsyncRelayCommand&lt;T&gt; naturally.
/// </summary>
public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _executeAsync;
    private readonly Func<bool>? _canExecute;

    private bool _isExecuting;

    public AsyncRelayCommand(Func<T?, Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) =>
        !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            T? cast;

            if (parameter is null)
            {
                cast = default;
            }
            else if (parameter is T t)
            {
                cast = t;
            }
            else
            {
                // Best-effort conversion for common cases (e.g., string).
                cast = (T?)Convert.ChangeType(parameter, typeof(T));
            }

            await _executeAsync(cast).ConfigureAwait(false);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
