using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DynamoCopilot.Extension.Commands
{
    /// <summary>
    /// Minimal ICommand implementation that wraps an async delegate.
    /// CanExecute can be updated by calling RaiseCanExecuteChanged().
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task>  _execute;
        private readonly Func<bool>? _canExecute;
        private bool                 _isRunning;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute    = execute    ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
            => !_isRunning && (_canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            _isRunning = true;
            RaiseCanExecuteChanged();
            try   { await _execute(); }
            finally
            {
                _isRunning = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
            => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Synchronous relay command for simple actions.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action      _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute    = execute    ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();

        public void RaiseCanExecuteChanged()
            => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
