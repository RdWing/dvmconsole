using Avalonia.Controls;
using DvmConsole.Application;

namespace DvmConsole.Desktop;

/// <summary>
/// Converts the Avalonia desktop window lifecycle into the portable host
/// contract. Mobile hosts can supply their own foreground/background adapter.
/// </summary>
internal sealed class DesktopApplicationLifecycle : IApplicationLifecycle, IDisposable
{
    private readonly Window window;
    private int active;
    private int stopping;
    private int disposed;

    public DesktopApplicationLifecycle(Window window)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        window.Activated += HandleActivated;
        window.Deactivated += HandleDeactivated;
        window.Closing += HandleClosing;
    }

    public bool IsActive => Volatile.Read(ref active) != 0;

    public event EventHandler? Activated;
    public event EventHandler? Deactivated;
    public event EventHandler? Suspending;
    public event EventHandler? Resumed;
    public event EventHandler? Stopping;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        window.Activated -= HandleActivated;
        window.Deactivated -= HandleDeactivated;
        window.Closing -= HandleClosing;
    }

    private void HandleActivated(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref active, 1) == 0)
            Activated?.Invoke(this, EventArgs.Empty);
    }

    private void HandleDeactivated(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref active, 0) != 0)
            Deactivated?.Invoke(this, EventArgs.Empty);
    }

    private void HandleClosing(object? sender, WindowClosingEventArgs args)
    {
        if (Interlocked.Exchange(ref stopping, 1) == 0)
            Stopping?.Invoke(this, EventArgs.Empty);
    }

    // Desktop has no ordinary application-suspension callback. These methods
    // keep the mapping explicit for a future host/lifetime integration and
    // provide one path for platform-specific desktop suspension if added.
    internal void NotifySuspending()
        => Suspending?.Invoke(this, EventArgs.Empty);

    internal void NotifyResumed()
        => Resumed?.Invoke(this, EventArgs.Empty);
}
