namespace DvmConsole.Application;

public sealed record RadioConnectionEndpoint(
    SystemId Id,
    string Name,
    Func<bool> IsActive,
    Func<CancellationToken, ValueTask> StartAsync,
    Func<CancellationToken, ValueTask> StopAsync);

public enum RadioConnectionTransitionKind
{
    StartingAll,
    StartedAll,
    StoppingAll,
    StoppedAll,
    StartingSystem,
    StoppingSystem,
    SystemStopped,
    SystemStartFaulted,
    SystemStopFaulted
}

public sealed record RadioConnectionTransition(
    RadioConnectionTransitionKind Kind,
    SystemId? SystemId = null,
    string? SystemName = null,
    Exception? Exception = null);

/// <summary>
/// Owns serialized radio connection transitions without depending on a radio
/// protocol implementation or presentation object.
/// </summary>
public sealed class RadioConnectionCoordinator
{
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly object startupCancellationSync = new();
    private readonly HashSet<CancellationTokenSource> startupCancellations = [];
    private readonly IReadOnlyDictionary<SystemId, RadioConnectionEndpoint> endpoints;
    private readonly Func<CancellationToken, ValueTask> synchronizeDependentSources;
    private readonly Func<CancellationToken, ValueTask> stopDependentSources;
    private readonly Action stopForwarding;
    private readonly Action<bool> setBusy;
    private readonly Action<RadioConnectionTransition> publishTransition;

    public RadioConnectionCoordinator(
        IEnumerable<RadioConnectionEndpoint> endpoints,
        Func<CancellationToken, ValueTask> synchronizeDependentSources,
        Func<CancellationToken, ValueTask> stopDependentSources,
        Action stopForwarding,
        Action<bool> setBusy,
        Action<RadioConnectionTransition> publishTransition)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        this.endpoints = endpoints.ToDictionary(endpoint => endpoint.Id);
        this.synchronizeDependentSources = synchronizeDependentSources ??
            throw new ArgumentNullException(nameof(synchronizeDependentSources));
        this.stopDependentSources = stopDependentSources ??
            throw new ArgumentNullException(nameof(stopDependentSources));
        this.stopForwarding = stopForwarding ?? throw new ArgumentNullException(nameof(stopForwarding));
        this.setBusy = setBusy ?? throw new ArgumentNullException(nameof(setBusy));
        this.publishTransition = publishTransition ?? throw new ArgumentNullException(nameof(publishTransition));
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => RunStartupTransitionAsync(ConnectCoreAsync, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelStartupTransitions();
        return RunExclusiveAsync(
            () => DisconnectCoreAsync(cancellationToken),
            cancellationToken);
    }

    public Task ToggleAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        if (!endpoints.TryGetValue(systemId, out RadioConnectionEndpoint? endpoint))
            throw new KeyNotFoundException($"Unknown radio system ID '{systemId}'.");

        return RunStartupTransitionAsync(
            token => ToggleCoreAsync(endpoint, token),
            cancellationToken);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        setBusy(true);
        publishTransition(new RadioConnectionTransition(RadioConnectionTransitionKind.StartingAll));
        try
        {
            await Task.WhenAll(endpoints.Values.Select(endpoint =>
                StartEndpointAsync(endpoint, cancellationToken)));
            cancellationToken.ThrowIfCancellationRequested();
            await synchronizeDependentSources(cancellationToken).ConfigureAwait(false);
            publishTransition(new RadioConnectionTransition(RadioConnectionTransitionKind.StartedAll));
        }
        finally
        {
            setBusy(false);
        }
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        setBusy(true);
        publishTransition(new RadioConnectionTransition(RadioConnectionTransitionKind.StoppingAll));
        try
        {
            await stopDependentSources(cancellationToken).ConfigureAwait(false);
            stopForwarding();
            await Task.WhenAll(endpoints.Values.Select(endpoint =>
                endpoint.StopAsync(cancellationToken).AsTask()));
            publishTransition(new RadioConnectionTransition(RadioConnectionTransitionKind.StoppedAll));
        }
        finally
        {
            setBusy(false);
        }
    }

    private async Task ToggleCoreAsync(
        RadioConnectionEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint.IsActive())
        {
            publishTransition(new RadioConnectionTransition(
                RadioConnectionTransitionKind.StoppingSystem,
                endpoint.Id,
                endpoint.Name));
            try
            {
                await endpoint.StopAsync(cancellationToken).ConfigureAwait(false);
                publishTransition(new RadioConnectionTransition(
                    RadioConnectionTransitionKind.SystemStopped,
                    endpoint.Id,
                    endpoint.Name));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                publishTransition(new RadioConnectionTransition(
                    RadioConnectionTransitionKind.SystemStopFaulted,
                    endpoint.Id,
                    endpoint.Name,
                    exception));
            }
            return;
        }

        publishTransition(new RadioConnectionTransition(
            RadioConnectionTransitionKind.StartingSystem,
            endpoint.Id,
            endpoint.Name));
        await StartEndpointAsync(endpoint, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await synchronizeDependentSources(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartEndpointAsync(
        RadioConnectionEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            await endpoint.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            publishTransition(new RadioConnectionTransition(
                RadioConnectionTransitionKind.SystemStartFaulted,
                endpoint.Id,
                endpoint.Name,
                exception));
        }
    }

    private async Task RunStartupTransitionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
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
        CancellationToken cancellationToken)
    {
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
}
