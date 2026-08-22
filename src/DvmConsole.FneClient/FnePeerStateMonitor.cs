using fnecore;

namespace DvmConsole.FneClient;

internal sealed record FneMonitoredState(FneConnectionState State, string Message);

internal sealed class FnePeerStateMonitor : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Task? monitorTask;

    public void Start(
        FnePeer peer,
        Func<FneConnectionState> getPublishedState,
        Action<FneConnectionState, string> publish)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(getPublishedState);
        ArgumentNullException.ThrowIfNull(publish);

        var nextCancellation = new CancellationTokenSource();
        lock (sync)
        {
            if (monitorTask is not null)
            {
                nextCancellation.Dispose();
                throw new InvalidOperationException("The FNE peer-state monitor is already running.");
            }

            cancellation = nextCancellation;
            monitorTask = MonitorAsync(
                peer,
                getPublishedState,
                publish,
                nextCancellation.Token);
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? currentCancellation;
        lock (sync)
        {
            currentCancellation = cancellation;
            cancellation = null;
            monitorTask = null;
        }

        currentCancellation?.Cancel();
        currentCancellation?.Dispose();
    }

    public async ValueTask StopAsync()
    {
        CancellationTokenSource? currentCancellation;
        Task? currentTask;
        lock (sync)
        {
            currentCancellation = cancellation;
            currentTask = monitorTask;
            cancellation = null;
            monitorTask = null;
        }

        currentCancellation?.Cancel();
        if (currentTask is not null)
        {
            try
            {
                await currentTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }
        currentCancellation?.Dispose();
    }

    public ValueTask DisposeAsync() => StopAsync();

    public static FneMonitoredState Interpret(ConnectionState state)
    {
        FneConnectionState connectionState = state switch
        {
            ConnectionState.WAITING_AUTHORISATION => FneConnectionState.Authenticating,
            ConnectionState.WAITING_CONFIG => FneConnectionState.Configuring,
            ConnectionState.RUNNING => FneConnectionState.Connected,
            _ => FneConnectionState.WaitingForLogin
        };

        return new FneMonitoredState(connectionState, connectionState switch
        {
            FneConnectionState.Authenticating => "FNE login accepted; waiting for authorization",
            FneConnectionState.Configuring => "FNE authorization accepted; sending configuration",
            FneConnectionState.Connected => "FNE peer connected",
            _ => "Waiting for FNE login acknowledgement"
        });
    }

    public static bool ShouldPublish(
        FneConnectionState nextState,
        FneConnectionState? lastState,
        FneConnectionState publishedState)
        => nextState != lastState || nextState != publishedState;

    private static async Task MonitorAsync(
        FnePeer peer,
        Func<FneConnectionState> getPublishedState,
        Action<FneConnectionState, string> publish,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        FneConnectionState? lastState = null;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                FneMonitoredState next = Interpret(peer.Information?.State ?? ConnectionState.WAITING_LOGIN);
                if (!ShouldPublish(next.State, lastState, getPublishedState()))
                    continue;

                lastState = next.State;
                publish(next.State, next.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }
}
