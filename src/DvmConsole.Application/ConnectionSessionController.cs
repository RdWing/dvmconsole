// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal interface IConnectionAdmissionPort
{
    bool IsStopping { get; }
}

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
    void SelectSystem(SystemId systemId);
    void PublishStartFault(SystemId systemId, string message);
}

/// <summary>
/// Coordinates connection commands and their operator-facing status through
/// protocol-neutral endpoints and host presentation callbacks.
/// </summary>
internal sealed class ConnectionSessionController
{
    private readonly IConnectionPresentationPort presentation;
    private readonly IReadOnlyDictionary<SystemId, RadioConnectionEndpoint> systemsById;
    private readonly RadioConnectionCoordinator inner;

    public ConnectionSessionController(
        IReadOnlyList<RadioConnectionEndpoint> systems,
        IConnectionPatchLifecyclePort patchLifecycle,
        IConnectionPresentationPort presentation, IConnectionAdmissionPort? admission = null)
    {
        ArgumentNullException.ThrowIfNull(systems);
        ArgumentNullException.ThrowIfNull(patchLifecycle);
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));

        systemsById = systems.ToDictionary(system => system.Id);
        inner = new RadioConnectionCoordinator(
            systems,
            patchLifecycle.SynchronizeAsync,
            patchLifecycle.StopSourcesAsync,
            patchLifecycle.StopForwarding,
            presentation.SetBusy,
            HandleTransition, admission is null ? null : () => admission.IsStopping);
    }

    public IReadOnlyList<RadioConnectionTransition> Abort() => inner.Abort();

    public IReadOnlyList<SystemId> CaptureActiveSystemIds() => inner.CaptureActiveSystemIds();

    public Task ConnectAsync()
        => inner.ConnectAsync();

    public Task ConnectAsync(CancellationToken cancellationToken)
        => inner.ConnectAsync(cancellationToken);

    public Task DisconnectAsync()
        => DisconnectAsync(CancellationToken.None);

    public Task DisconnectAsync(CancellationToken cancellationToken)
        => inner.DisconnectAsync(cancellationToken);

    public Task RestoreAsync(
        IEnumerable<SystemId> systemIds,
        CancellationToken cancellationToken = default)
        => inner.RestoreAsync(systemIds, cancellationToken);

    public Task ToggleAsync(SystemId systemId) => ToggleAsync(systemId, CancellationToken.None);

    public Task ToggleAsync(SystemId systemId, CancellationToken cancellationToken)
    {
        if (!systemsById.ContainsKey(systemId))
            throw new ArgumentException("The FNE is not part of this console.", nameof(systemId));
        presentation.SelectSystem(systemId);
        return inner.ToggleAsync(systemId, cancellationToken);
    }

    private void HandleTransition(RadioConnectionTransition transition)
    {
        if (transition.Kind == RadioConnectionTransitionKind.SystemStartFaulted)
            PublishStartFault(transition);
        else
            presentation.SetStatus(RadioConnectionStatus.Format(transition));
    }

    private void PublishStartFault(RadioConnectionTransition transition)
    {
        if (transition.SystemId is SystemId systemId && systemsById.ContainsKey(systemId))
            presentation.PublishStartFault(systemId,
                transition.Exception?.Message ?? "The radio connection could not start.");
    }
}
