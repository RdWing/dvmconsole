using Avalonia.Threading;

namespace DvmConsole.Desktop;

public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action, bool background = false);
    ValueTask InvokeAsync(Action action);
}

internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static AvaloniaUiDispatcher Instance { get; } = new();

    private AvaloniaUiDispatcher()
    {
    }

    public bool CheckAccess()
        => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action, bool background = false)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.Post(
            action,
            background ? DispatcherPriority.Background : DispatcherPriority.Normal);
    }

    public async ValueTask InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }
}
