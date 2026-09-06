// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal interface IConnectionPatchLifecyclePort
{
    ValueTask SynchronizeAsync(CancellationToken cancellationToken);
    ValueTask StopSourcesAsync(CancellationToken cancellationToken);
    void StopForwarding();
}

internal interface IConnectionPresentationPort
{
    void SetBusy(bool value);
    void SetStatus(string value);
    void SelectSystem(SystemViewModel system);
    void PublishStatus(SystemViewModel system, FneConnectionStatus status);
}

internal sealed class ConnectionPatchLifecyclePort(
    Func<CancellationToken, Task> synchronize,
    Func<CancellationToken, Task> stopSources,
    Action stopForwarding) : IConnectionPatchLifecyclePort
{
    public ValueTask SynchronizeAsync(CancellationToken cancellationToken)
        => new(synchronize(cancellationToken));

    public ValueTask StopSourcesAsync(CancellationToken cancellationToken)
        => new(stopSources(cancellationToken));

    public void StopForwarding() => stopForwarding();
}

internal sealed class ConnectionPresentationPort(
    Action<bool> setBusy,
    Action<string> setStatus,
    Action<SystemViewModel> selectSystem,
    Action<SystemViewModel, FneConnectionStatus> publishStatus) : IConnectionPresentationPort
{
    public void SetBusy(bool value) => setBusy(value);
    public void SetStatus(string value) => setStatus(value);
    public void SelectSystem(SystemViewModel system) => selectSystem(system);
    public void PublishStatus(SystemViewModel system, FneConnectionStatus status)
        => publishStatus(system, status);
}

/// <summary>
/// Adapts the portable radio connection coordinator to the current desktop
/// FNE view models and preserves the established operator-facing status text.
/// </summary>
internal sealed class ConnectionSessionController
{
    private readonly IReadOnlyList<SystemViewModel> systems;
    private readonly IConnectionPresentationPort presentation;
    private readonly IReadOnlyDictionary<SystemId, SystemViewModel> systemsById;
    private readonly RadioConnectionCoordinator inner;

    public ConnectionSessionController(
        IReadOnlyList<SystemViewModel> systems,
        IConnectionPatchLifecyclePort patchLifecycle,
        IConnectionPresentationPort presentation)
    {
        this.systems = systems ?? throw new ArgumentNullException(nameof(systems));
        ArgumentNullException.ThrowIfNull(patchLifecycle);
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));

        systemsById = systems.ToDictionary(system => SystemId.FromName(system.Name));
        inner = new RadioConnectionCoordinator(
            systems.Select(system => new RadioConnectionEndpoint(
                SystemId.FromName(system.Name),
                system.Name,
                () => system.IsConnectionActive,
                cancellationToken => new ValueTask(system.StartAsync(cancellationToken)),
                cancellationToken => new ValueTask(system.StopAsync(cancellationToken)))),
            patchLifecycle.SynchronizeAsync,
            patchLifecycle.StopSourcesAsync,
            patchLifecycle.StopForwarding,
            presentation.SetBusy,
            HandleTransition);
    }

    public Task ConnectAsync()
        => inner.ConnectAsync();

    public Task DisconnectAsync()
        => DisconnectAsync(CancellationToken.None);

    public Task DisconnectAsync(CancellationToken cancellationToken)
        => inner.DisconnectAsync(cancellationToken);

    public Task RestoreAsync(
        IEnumerable<SystemId> systemIds,
        CancellationToken cancellationToken = default)
        => inner.RestoreAsync(systemIds, cancellationToken);

    public Task ToggleAsync(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!systems.Contains(system))
            throw new ArgumentException("The FNE is not part of this console.", nameof(system));

        presentation.SelectSystem(system);
        return inner.ToggleAsync(SystemId.FromName(system.Name));
    }

    private void HandleTransition(RadioConnectionTransition transition)
    {
        switch (transition.Kind)
        {
            case RadioConnectionTransitionKind.StartingAll:
                presentation.SetStatus("Starting FNE connection services...");
                break;
            case RadioConnectionTransitionKind.StartedAll:
                presentation.SetStatus("FNE connection services started; waiting for login acknowledgements.");
                break;
            case RadioConnectionTransitionKind.StoppingAll:
                presentation.SetStatus("Stopping FNE connection services...");
                break;
            case RadioConnectionTransitionKind.StoppedAll:
                presentation.SetStatus("FNE connections stopped.");
                break;
            case RadioConnectionTransitionKind.StartingSystem:
                presentation.SetStatus($"Starting {transition.SystemName}...");
                break;
            case RadioConnectionTransitionKind.StoppingSystem:
                presentation.SetStatus($"Stopping {transition.SystemName}...");
                break;
            case RadioConnectionTransitionKind.SystemStopped:
                presentation.SetStatus($"{transition.SystemName}: disconnected.");
                break;
            case RadioConnectionTransitionKind.SystemStartFaulted:
                PublishStartFault(transition);
                break;
            case RadioConnectionTransitionKind.SystemStopFaulted:
                presentation.SetStatus($"{transition.SystemName}: disconnect failed — {transition.Exception?.Message}");
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

        presentation.PublishStatus(system, new FneConnectionStatus(
            system.Name,
            FneConnectionState.Faulted,
            transition.Exception?.Message ?? "The radio connection could not start.",
            DateTimeOffset.UtcNow));
    }
}
