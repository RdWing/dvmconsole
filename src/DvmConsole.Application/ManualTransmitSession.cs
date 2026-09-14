// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Application;

internal sealed record ManualTransmitPolicy(
    ConsoleTransmitChannelDirectory Channels,
    Func<string, IRadioTrafficEndpoint?> ResolveSystem,
    Func<bool> IsInputSuppressed,
    Func<bool> PlayPermitTone,
    Action<string> SetStatus,
    Action<IReadOnlyList<ChannelId>> Starting,
    bool NetworkDisabled = false);

internal sealed record ManualTransmitStatePorts(
    ConsoleTransmitState State,
    Action<Action> Apply,
    Action<ChannelId, bool>? Changed = null,
    Action<TransmitTarget, uint, ConsoleCallHistoryRecord, TransmitStartupDiagnostics, Func<TimeSpan>>? Started = null,
    Action<TransmitStream, CallId?>? Completed = null);

/// <summary>
/// Owns manual TX control, history and recording transitions. Host callbacks
/// observe committed state; they cannot omit a transition by delaying UI work.
/// </summary>
internal sealed class ManualTransmitSession(
    ManualTransmitPolicy policy,
    ITransmitLifecyclePresentation presentation,
    ManualTransmitStatePorts ports,
    ChannelTransmitCoordinator microphone) : IManualTransmitStartContext, ITransmitLifecyclePresentation
{
    public bool IsInputSuppressed => policy.IsInputSuppressed();
    public bool NetworkDisabled => policy.NetworkDisabled;
    public bool HasActiveTransmission => microphone.ActiveChannel is not null;
    public bool PlayPermitTone => policy.PlayPermitTone();
    public TransmitChannelDescriptor CaptureChannel(ChannelId id) => policy.Channels.Capture(id);
    public IRadioTrafficEndpoint? ResolveSystem(string name) => policy.ResolveSystem(name);
    public Task SetStatusAsync(string text)
    {
        policy.SetStatus(text);
        return Task.CompletedTask;
    }

    public async Task<bool> BeginChannelAsync(ChannelId id, ManualTransmitCoordinator commands,
        Action? starting = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsInputSuppressed) return false;
        var channel = policy.Channels.State(id);
        if (channel.Operator.Snapshot.TransmitEnabled) return true;
        var descriptor = CaptureChannel(id);
        if (!policy.Channels.CanTransmit(id) || (descriptor.ReceiveActive && !descriptor.AllowsTransmitDuringReceive))
        {
            string reason = channel.Authority == TargetAuthorityState.Unavailable
                ? descriptor.AuthorityUnavailableReason : descriptor.ConfigurationUnavailableReason;
            policy.SetStatus(descriptor.ReceiveActive
                ? $"PTT unavailable: {descriptor.Name} is currently receiving."
                : $"PTT unavailable for {descriptor.Name}: {reason}.");
            return false;
        }
        starting?.Invoke();
        await commands.StartAsync([id], cancellationToken).ConfigureAwait(false);
        return channel.Operator.Snapshot.TransmitEnabled;
    }

    public Task EndChannelAsync(ChannelId id, ManualTransmitCoordinator commands)
    {
        // Releasing a pending press must revoke it before microphone readiness.
        // An unrelated channel's release must not cancel the current startup.
        commands.CancelChannelStartup(id);
        return policy.Channels.State(id).Operator.Snapshot.TransmitEnabled
            ? commands.StopAsync([id], propagateUnconfirmedStop: true)
            : Task.CompletedTask;
    }

    public Task SetStartingAsync(IReadOnlyList<ChannelId> ids)
    {
        ports.Apply(() => ports.State.Starting(ids, id => ports.Changed?.Invoke(id, false)));
        policy.Starting(ids);
        return Task.CompletedTask;
    }

    public Task StartedAsync(IReadOnlyList<TransmitTarget> targets, IReadOnlyList<ChannelId> active,
        TransmitStartupDiagnostics diagnostics, Func<TimeSpan> elapsed)
    {
        ports.Apply(() => ports.State.Started(targets, active, microphone.GetActiveStreamId, () => Now,
            (target, stream, record) =>
            {
                ports.Changed?.Invoke(target.Channel.Id, false);
                ports.Started?.Invoke(target, stream, record, diagnostics, elapsed);
            }));
        return presentation.StartedAsync(targets, active, diagnostics, elapsed);
    }

    public Task StartFailedAsync(IReadOnlyList<ChannelId> ids, Exception failure)
    {
        ports.Apply(() => ports.State.StartFailed(ids, id => ports.Changed?.Invoke(id, true)));
        return presentation.StartFailedAsync(ids, failure);
    }

    public Task StoppingAsync(IReadOnlyList<TransmitStream> streams)
    {
        ports.Apply(() => ports.State.Stopping(streams, id => ports.Changed?.Invoke(id, false)));
        return presentation.StoppingAsync(streams);
    }

    public Task StoppedAsync(IReadOnlyList<ChannelId> ids, IReadOnlyList<TransmitStream> streams,
        IReadOnlySet<ChannelId> unresolved, TimeSpan elapsed, Exception? failure, string? status)
    {
        ports.Apply(() => ports.State.Stopped(ids, streams, unresolved, () => Now,
            ports.Changed, ports.Completed));
        return presentation.StoppedAsync(ids, streams, unresolved, elapsed, failure, status);
    }

    public DateTimeOffset Now => presentation.Now;
    public long GetTimestamp() => presentation.GetTimestamp();
    public TimeSpan GetElapsedTime(long started) => presentation.GetElapsedTime(started);
    public bool MuteReceiveWhileTransmitting => presentation.MuteReceiveWhileTransmitting;
    public bool? SelectedMicrophoneIsBluetooth => presentation.SelectedMicrophoneIsBluetooth;
    public void ClearActivation() => presentation.ClearActivation();
    public void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message)
        => presentation.Log(timestamp, source, severity, message);
}
