using Avalonia.Threading;
using System.Windows.Input;

namespace DvmConsole.Desktop;

internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool> canExecute;
    private readonly Func<bool> checkUiAccess;
    private readonly Action<Action> postToUi;
    private bool running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute)
        : this(
            execute,
            canExecute,
            Dispatcher.UIThread.CheckAccess,
            action => Dispatcher.UIThread.Post(action))
    {
    }

    internal AsyncRelayCommand(
        Func<Task> execute,
        Func<bool> canExecute,
        Func<bool> checkUiAccess,
        Action<Action> postToUi)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        this.checkUiAccess = checkUiAccess ?? throw new ArgumentNullException(nameof(checkUiAccess));
        this.postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;
        running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        if (checkUiAccess())
        {
            RaiseCanExecuteChangedCore();
            return;
        }

        postToUi(RaiseCanExecuteChangedCore);
    }

    private void RaiseCanExecuteChangedCore() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
