namespace DvmConsole.Desktop;

// Prevents work queued by a session-owned service from reaching the UI after
// that session has begun disposal. Close waits for an executing callback, so
// callback code and session resource disposal cannot overlap.
internal sealed class SessionUiCallbackGate
{
    private readonly object sync = new();
    private readonly IUiDispatcher dispatcher;
    private bool closed;

    public SessionUiCallbackGate(IUiDispatcher dispatcher)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Post(Action callback, bool background = false)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (sync)
        {
            if (closed)
                return;
        }

        dispatcher.Post(
            () =>
            {
                lock (sync)
                {
                    if (closed)
                        return;

                    callback();
                }
            },
            background);
    }

    public void Close()
    {
        lock (sync)
            closed = true;
    }
}
