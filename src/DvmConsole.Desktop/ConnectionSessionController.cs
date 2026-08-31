using DvmConsole.Application;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

/// <summary>
/// Adapts the portable radio connection coordinator to the current desktop
/// FNE view models and preserves the established operator-facing status text.
/// </summary>
internal sealed class ConnectionSessionController
{
    private readonly IReadOnlyList<SystemViewModel> systems;
    private readonly Action<string> setStatus;
    private readonly Action<SystemViewModel> selectSystem;
    private readonly Action<SystemViewModel, FneConnectionStatus> publishStatus;
    private readonly IReadOnlyDictionary<SystemId, SystemViewModel> systemsById;
    private readonly RadioConnectionCoordinator inner;

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
        ArgumentNullException.ThrowIfNull(synchronizePatchSources);
        ArgumentNullException.ThrowIfNull(stopPatchSources);
        ArgumentNullException.ThrowIfNull(stopPatchForwarding);
        ArgumentNullException.ThrowIfNull(setBusy);
        this.setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        this.selectSystem = selectSystem ?? throw new ArgumentNullException(nameof(selectSystem));
        this.publishStatus = publishStatus ?? throw new ArgumentNullException(nameof(publishStatus));

        systemsById = systems.ToDictionary(system => SystemId.FromName(system.Name));
        inner = new RadioConnectionCoordinator(
            systems.Select(system => new RadioConnectionEndpoint(
                SystemId.FromName(system.Name),
                system.Name,
                () => system.IsConnectionActive,
                cancellationToken => new ValueTask(system.StartAsync(cancellationToken)),
                cancellationToken => new ValueTask(system.StopAsync(cancellationToken)))),
            cancellationToken => new ValueTask(synchronizePatchSources(cancellationToken)),
            cancellationToken => new ValueTask(stopPatchSources(cancellationToken)),
            stopPatchForwarding,
            setBusy,
            HandleTransition);
    }

    public Task ConnectAsync()
        => inner.ConnectAsync();

    public Task DisconnectAsync()
        => DisconnectAsync(CancellationToken.None);

    public Task DisconnectAsync(CancellationToken cancellationToken)
        => inner.DisconnectAsync(cancellationToken);

    public Task ToggleAsync(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!systems.Contains(system))
            throw new ArgumentException("The FNE is not part of this console.", nameof(system));

        selectSystem(system);
        return inner.ToggleAsync(SystemId.FromName(system.Name));
    }

    private void HandleTransition(RadioConnectionTransition transition)
    {
        switch (transition.Kind)
        {
            case RadioConnectionTransitionKind.StartingAll:
                setStatus("Starting FNE connection services...");
                break;
            case RadioConnectionTransitionKind.StartedAll:
                setStatus("FNE connection services started; waiting for login acknowledgements.");
                break;
            case RadioConnectionTransitionKind.StoppingAll:
                setStatus("Stopping FNE connection services...");
                break;
            case RadioConnectionTransitionKind.StoppedAll:
                setStatus("FNE connections stopped.");
                break;
            case RadioConnectionTransitionKind.StartingSystem:
                setStatus($"Starting {transition.SystemName}...");
                break;
            case RadioConnectionTransitionKind.StoppingSystem:
                setStatus($"Stopping {transition.SystemName}...");
                break;
            case RadioConnectionTransitionKind.SystemStopped:
                setStatus($"{transition.SystemName}: disconnected.");
                break;
            case RadioConnectionTransitionKind.SystemStartFaulted:
                PublishStartFault(transition);
                break;
            case RadioConnectionTransitionKind.SystemStopFaulted:
                setStatus($"{transition.SystemName}: disconnect failed — {transition.Exception?.Message}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition));
        }
    }

    private void PublishStartFault(RadioConnectionTransition transition)
    {
        if (transition.SystemId is not SystemId systemId ||
            !systemsById.TryGetValue(systemId, out SystemViewModel? system))
        {
            return;
        }

        publishStatus(system, new FneConnectionStatus(
            system.Name,
            FneConnectionState.Faulted,
            transition.Exception?.Message ?? "The radio connection could not start.",
            DateTimeOffset.UtcNow));
    }
}
