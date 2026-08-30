using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal sealed class ConnectionSessionController
{
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly object startupCancellationSync = new();
    private readonly HashSet<CancellationTokenSource> startupCancellations = [];
    private readonly IReadOnlyList<SystemViewModel> systems;
    private readonly Func<CancellationToken, Task> synchronizePatchSources;
    private readonly Func<CancellationToken, Task> stopPatchSources;
    private readonly Action stopPatchForwarding;
    private readonly Action<bool> setBusy;
    private readonly Action<string> setStatus;
    private readonly Action<SystemViewModel> selectSystem;
    private readonly Action<SystemViewModel, FneConnectionStatus> publishStatus;

    public ConnectionSessionController(
        IReadOnlyList<SystemViewModel> systems,
        Func<CancellationToken, Task> synchronizePatchSources,
        Func<CancellationToken, Task> stopPatchSources,
        Action stopPatchForwarding,
        Action<bool> setBusy,
        Action<string> setStatus,
        Action<SystemViewModel> selectSystem,
        Action<SystemViewModel, FneConnectionStatus> publishStatus)
    {
        this.systems = systems ?? throw new ArgumentNullException(nameof(systems));
        this.synchronizePatchSources = synchronizePatchSources ?? throw new ArgumentNullException(nameof(synchronizePatchSources));
        this.stopPatchSources = stopPatchSources ?? throw new ArgumentNullException(nameof(stopPatchSources));
        this.stopPatchForwarding = stopPatchForwarding ?? throw new ArgumentNullException(nameof(stopPatchForwarding));
        this.setBusy = setBusy ?? throw new ArgumentNullException(nameof(setBusy));
        this.setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        this.selectSystem = selectSystem ?? throw new ArgumentNullException(nameof(selectSystem));
        this.publishStatus = publishStatus ?? throw new ArgumentNullException(nameof(publishStatus));
    }

    public Task ConnectAsync()
        => RunStartupTransitionAsync(ConnectCoreAsync);

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        setBusy(true);
        setStatus("Starting FNE connection services...");
        try
        {
            await Task.WhenAll(systems.Select(system => StartSystemAsync(system, cancellationToken)));
            cancellationToken.ThrowIfCancellationRequested();
            await synchronizePatchSources(cancellationToken).ConfigureAwait(false);
            setStatus("FNE connection services started; waiting for login acknowledgements.");
        }
        finally
        {
            setBusy(false);
        }
    }

    public Task DisconnectAsync()
        => DisconnectAsync(CancellationToken.None);

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        CancelStartupTransitions();
        return RunExclusiveAsync(
            () => DisconnectCoreAsync(cancellationToken),
            cancellationToken);
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        setBusy(true);
        setStatus("Stopping FNE connection services...");
        try
        {
            await stopPatchSources(cancellationToken).ConfigureAwait(false);
            stopPatchForwarding();
            await Task.WhenAll(systems.Select(system => system.StopAsync(cancellationToken)));
            setStatus("FNE connections stopped.");
        }
        finally
        {
            setBusy(false);
        }
    }

    public Task ToggleAsync(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!systems.Contains(system))
            throw new ArgumentException("The FNE is not part of this console.", nameof(system));

        return RunStartupTransitionAsync(
            cancellationToken => ToggleCoreAsync(system, cancellationToken));
    }

    private async Task ToggleCoreAsync(
        SystemViewModel system,
        CancellationToken cancellationToken)
    {
        selectSystem(system);
        if (system.IsConnectionActive)
        {
            setStatus($"Stopping {system.Name}...");
            try
            {
                await system.StopAsync(cancellationToken);
                setStatus($"{system.Name}: disconnected.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                setStatus($"{system.Name}: disconnect failed — {exception.Message}");
            }
            return;
        }

        setStatus($"Starting {system.Name}...");
        await StartSystemAsync(system, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await synchronizePatchSources(cancellationToken);
    }

    private async Task RunStartupTransitionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource startupCancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        lock (startupCancellationSync)
            startupCancellations.Add(startupCancellation);

        try
        {
            await RunExclusiveAsync(
                () => operation(startupCancellation.Token),
                startupCancellation.Token);
        }
        finally
        {
            lock (startupCancellationSync)
                startupCancellations.Remove(startupCancellation);
        }
    }

    private void CancelStartupTransitions()
    {
        CancellationTokenSource[] pending;
        lock (startupCancellationSync)
            pending = startupCancellations.ToArray();

        foreach (CancellationTokenSource cancellation in pending)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A completed startup can leave the snapshot while cancellation
                // is being delivered. Its transition no longer needs preemption.
            }
        }
    }

    private async Task RunExclusiveAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        // These callbacks update UI-bound state. Preserve the caller's
        // synchronization context even when this transition had to wait for
        // an earlier connect or disconnect to finish.
        await transitionGate.WaitAsync(cancellationToken);
        try
        {
            await operation();
        }
        finally
        {
            transitionGate.Release();
        }
    }

    private async Task StartSystemAsync(
        SystemViewModel system,
        CancellationToken cancellationToken)
    {
        try
        {
            await system.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            publishStatus(system, new FneConnectionStatus(
                system.Name,
                FneConnectionState.Faulted,
                exception.Message,
                DateTimeOffset.UtcNow));
        }
    }
}
