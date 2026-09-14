// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed class ConnectionAdmissionPort(Func<bool> isStopping) : IConnectionAdmissionPort
{
    public bool IsStopping => isStopping();
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
    Action<SystemId> selectSystem,
    Action<SystemId, string> publishStartFault) : IConnectionPresentationPort
{
    public void SetBusy(bool value) => setBusy(value);
    public void SetStatus(string value) => setStatus(value);
    public void SelectSystem(SystemId systemId) => selectSystem(systemId);
    public void PublishStartFault(SystemId systemId, string message)
        => publishStartFault(systemId, message);
}
