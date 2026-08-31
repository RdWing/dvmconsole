namespace DvmConsole.Ptt;

// A lifecycle-bound PTT source controlled by the host UI or a future keyboard
// adapter. It provides a deterministic state boundary without assuming a
// platform-specific global hotkey implementation.
public sealed class ManualPttSource : IPttSource
{
    private bool started;
    private bool disposed;

    public event EventHandler<bool>? StateChanged;
    public bool IsPressed { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        started = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (started)
        {
            SetPressed(false);
            started = false;
        }
        return ValueTask.CompletedTask;
    }

    public void SetPressed(bool pressed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
            throw new InvalidOperationException("The PTT source must be started before changing its state.");
        if (IsPressed == pressed)
            return;

        IsPressed = pressed;
        StateChanged?.Invoke(this, pressed);
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            if (started)
                SetPressed(false);
            started = false;
            disposed = true;
        }
        return ValueTask.CompletedTask;
    }
}
