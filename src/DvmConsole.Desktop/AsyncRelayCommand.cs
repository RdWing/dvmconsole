using Avalonia.Threading;
using System.Windows.Input;

namespace DvmConsole.Desktop;

internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool> canExecute;
    private readonly Func<bool> checkUiAccess;
    private readonly Action<Action> postToUi;
    private readonly Action<Exception> reportFault;
    private bool running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute)
        : this(
            execute,
            canExecute,
            Dispatcher.UIThread.CheckAccess,
            action => Dispatcher.UIThread.Post(action),
            exception => DesktopCrashLog.Write("Async command", exception))
    {
    }

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool> canExecute,
        Action<Exception> reportFault)
        : this(
            execute,
            canExecute,
            Dispatcher.UIThread.CheckAccess,
            action => Dispatcher.UIThread.Post(action),
            reportFault)
    {
    }

    internal AsyncRelayCommand(
        Func<Task> execute,
        Func<bool> canExecute,
        Func<bool> checkUiAccess,
        Action<Action> postToUi,
        Action<Exception>? reportFault = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        this.checkUiAccess = checkUiAccess ?? throw new ArgumentNullException(nameof(checkUiAccess));
        this.postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        this.reportFault = reportFault ?? (exception => DesktopCrashLog.Write("Async command", exception));
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
        catch (OperationCanceledException)
        {
            // Cancellation is an expected command completion state.
        }
        catch (Exception exception)
        {
            try
            {
                reportFault(exception);
            }
            catch (Exception reportingException)
            {
                DesktopCrashLog.Write(
                    "Async command fault reporting",
                    new AggregateException(reportingException, exception));
            }
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
