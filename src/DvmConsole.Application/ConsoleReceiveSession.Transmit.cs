// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : ITransmitLifecyclePresentation, IConsoleSelectedTransmitCommands
{
    private ConsoleTransmitRuntime? transmit;
    internal ManualTransmitSession? ManualTransmitSession => transmit?.Session;
    private SemaphoreSlim manualCommands = null!;
    private SemaphoreSlim transmitAdmission = null!;
    private Task? manualRelease;
    private int radioTransitions;
    private sealed record ManualOwner(ChannelId? Channel, ChannelId[] Targets);
    private ManualOwner? manualOwner;
    public bool IsSelectedPttRequested { get { lock (ingressSync) return manualOwner is { Channel: null }; } }

    private ConsoleLiveTransmitPorts? PrepareManualTransmit()
    {
        if (dependencies.ManualInput is not { } input) return null;
        microphoneProcessing = input.Normalize();
        var host = dependencies.Host;
        transmit = operationalRuntime.Transmit;
        manualCommands = new(1, 1);
        transmitAdmission = new(1, 1);
        services.Transmit.Own("manual-command-gate", manualCommands);
        services.Transmit.Own("manual-admission-gate", transmitAdmission);
        operationalRuntime.RegisterTransmitOwnership("manual-runtime", manualCommands, transmitAdmission,
            () => Task.CompletedTask, CancelManualStartup);
        var ports = new ConsoleLiveTransmitPorts(new Keys(dependencies), input,
            new TransmitAudioBackendPort(() => host.AudioBackends.Create(AudioBackendConfiguration.Default), () => host.Vocoders.Create()),
            new TransmitSampleObservationPort(borrowed: ObserveTransmitSamples, fault: exception => ReportMediaFailure("TX", exception)),
            () => null, (id, enabled) =>
            {
                if (!enabled) PublishMeter(id, 0, 0);
                Changed(id);
            },
            new TransmitAudioPresentationPort(state.Media, ingressSync, id => Changed(id), Log, SetStatus, host.Clock),
            new TransmitAudioGate(reconfiguration), this, transmitAdmission,
            new ManualTransmitPolicy(transmitChannels,
                name => radios.Sessions.GetValueOrDefault(SystemId.FromName(name)),
                () => IsStopping || Volatile.Read(ref radioTransitions) != 0 || !state.Execution.Snapshot.CanTransmitManually,
                () => activeManualTransmitOptions.TalkPermitTone, SetStatus, _ => SetStatus("Starting PTT…")),
            () => { lock (ingressSync) EnsureToneAdmission(); }, () => LocalToneMonitorEnabled,
            new ConsoleManualTransmitObservers(
                (id, stopped) =>
                {
                    if (stopped) PublishMeter(id, 0, 0);
                    Changed(id);
                }), UseSharedCueOutput: true);
        services.Presentation.Register("manual-lifecycle-observers", () =>
        {
            if (transmit.Microphone is { } microphone) microphone.Faulted -= OnTransmitFault;
            host.Lifecycle.Activated -= OnTransmitForeground;
            host.Lifecycle.Deactivated -= OnTransmitBackground;
            return ValueTask.CompletedTask;
        });
        return ports;
    }

    private void AttachManualTransmit()
    {
        if (transmit is null) return;
        transmit.Microphone.Faulted += OnTransmitFault;
        var lifecycle = dependencies.Host.Lifecycle;
        lifecycle.Activated += OnTransmitForeground;
        lifecycle.Deactivated += OnTransmitBackground;
        state.Execution.SetForeground(lifecycle.IsActive);
    }

    private void OnTransmitForeground(object? sender, EventArgs args) => state.Execution.SetForeground(true);
    private void OnTransmitBackground(object? sender, EventArgs args)
    {
        state.Execution.SetForeground(false);
        DvmConsole.Threading.TaskObservation.Observe(CancelTonesAsync());
        RequestManualRelease();
    }
    private void OnTransmitFault(object? sender, Exception exception)
    {
        runtimeHealth.ObserveTransmitError(exception);
        SetStatus($"Microphone stopped: {exception.Message}");
        RequestManualRelease();
    }
    private void CancelManualStartup()
    {
        transmit?.Manual?.CancelStartup();
        transmit?.Microphone?.SetMicrophoneAudioSuppressed(true);
    }
    private void RequestManualRelease()
    {
        if (transmit is null) return;
        CancelManualStartup();
        DvmConsole.Threading.TaskObservation.Observe(ReleaseManualAsync());
    }
    private Task ReleaseManualAsync(ChannelId? owner = null, bool selectedOnly = false)
    {
        if (transmit is null) return Task.CompletedTask;
        lock (ingressSync)
        {
            // An active member's visible Release button also releases a selected call.
            // A stale channel release during selected startup must not revoke that new intent.
            if (owner is { } channel && manualOwner?.Channel != owner &&
                !(manualOwner is { Channel: null } && state.Channels[channel].Operator.Snapshot.TransmitEnabled))
                return Task.CompletedTask;
            if (selectedOnly && manualOwner is not { Channel: null }) return Task.CompletedTask;
            CancelManualStartup();
            return manualRelease is { IsCompleted: false } ? manualRelease : manualRelease = ReleaseManualCoreAsync();
        }
    }
    private async Task ReleaseManualCoreAsync()
    {
        await manualCommands.WaitAsync().ConfigureAwait(false);
        try
        {
            await transmit!.Manual.StopAsync(transmit.Microphone.ActiveChannels,
                propagateUnconfirmedStop: true).ConfigureAwait(false);
        }
        finally
        {
            lock (ingressSync) manualOwner = null;
            manualCommands.Release();
        }
    }

    public ValueTask<bool> BeginPttAsync(ChannelId id, CancellationToken cancellationToken = default)
        => BeginManualAsync(id, cancellationToken);

    public ValueTask<bool> BeginSelectedPttAsync(CancellationToken cancellationToken = default)
        => BeginManualAsync(null, cancellationToken);

    public ValueTask EndSelectedPttAsync(CancellationToken cancellationToken = default)
        => new(ReleaseManualAsync(selectedOnly: true));

    private async ValueTask<bool> BeginManualAsync(ChannelId? channelId, CancellationToken cancellationToken)
    {
        if (transmit is null || IsStopping || Volatile.Read(ref radioTransitions) != 0) return false;
        ConsoleExecutionLease? lease = state.Execution.TryAcquire(ConsoleTransmitIntent.Manual);
        if (lease is null || !await manualCommands.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            if (IsStopping || Volatile.Read(ref radioTransitions) != 0 || !state.Execution.IsCurrent(lease.Value)) return false;
            ChannelId[] targets;
            lock (ingressSync)
            {
                if (manualOwner is not null || manualRelease is { IsCompleted: false }) return false;
                if (channelId is { } id)
                {
                    _ = state.Channels[id];
                    targets = [id];
                }
                else
                {
                    targets = transmitChannels.SelectManualChannels();
                    if (targets.Length == 0) return false;
                }
                // Capture the selected set once. Later selection edits affect the next call only.
                manualOwner = new(channelId, targets);
                activeManualTransmitOptions = ManualTransmitOptions;
            }
            transmit.Microphone.UpdateAudioInputOptions(MicrophoneProcessing);
            if (channelId is { } single)
                await transmit.BeginChannelAsync(single, cancellationToken: cancellationToken).ConfigureAwait(false);
            else
                await transmit.Manual.StartAsync(targets, cancellationToken).ConfigureAwait(false);
            if (IsStopping || !state.Execution.IsCurrent(lease.Value))
            {
                await transmit.Manual.StopAsync(transmit.Microphone.ActiveChannels, propagateUnconfirmedStop: true).ConfigureAwait(false);
                return false;
            }
            return transmit.Microphone.ActiveChannels.Count > 0;
        }
        finally
        {
            lock (ingressSync)
                if (transmit.Microphone.ActiveChannels.Count == 0) manualOwner = null;
            manualCommands.Release();
        }
    }

    public ValueTask EndPttAsync(ChannelId id, CancellationToken cancellationToken = default)
        // Release is safety cleanup; a cancelled caller cannot suppress this edge.
        => new(ReleaseManualAsync(id));

    private void ObserveTransmitSamples(ChannelId id, uint stream, uint source, ReadOnlySpan<short> samples)
    {
        lock (ingressSync)
        {
            if (IsStopping) return;
            transmitState.ObserveSamples(id, stream, source, samples);
            if (meters.Observe(id, stream, samples, ChannelAudioDirection.Transmit)) meterWork?.Start();
        }
    }

    DateTimeOffset ITransmitLifecyclePresentation.Now => dependencies.Host.Clock.UtcNow;
    long ITransmitLifecyclePresentation.GetTimestamp() => time.GetTimestamp();
    TimeSpan ITransmitLifecyclePresentation.GetElapsedTime(long started) => time.GetElapsedTime(started);
    bool ITransmitLifecyclePresentation.MuteReceiveWhileTransmitting => activeManualTransmitOptions.MuteReceiveWhileTransmitting;
    bool? ITransmitLifecyclePresentation.SelectedMicrophoneIsBluetooth => null;
    Task ITransmitLifecyclePresentation.StartedAsync(IReadOnlyList<TransmitTarget> targets,
        IReadOnlyList<ChannelId> active, TransmitStartupDiagnostics diagnostics, Func<TimeSpan> elapsed)
    {
        SetStatus($"Transmitting on {active.Count} channel(s).");
        return Task.CompletedTask;
    }
    Task ITransmitLifecyclePresentation.StartFailedAsync(IReadOnlyList<ChannelId> ids, Exception failure)
    {
        if (state.Execution.Snapshot.CanReceive) SetStatus($"PTT stopped: {failure.Message}");
        return Task.CompletedTask;
    }
    Task ITransmitLifecyclePresentation.StoppingAsync(IReadOnlyList<TransmitStream> streams)
    {
        return Task.CompletedTask;
    }
    Task ITransmitLifecyclePresentation.StoppedAsync(IReadOnlyList<ChannelId> ids, IReadOnlyList<TransmitStream> streams,
        IReadOnlySet<ChannelId> unresolved, TimeSpan elapsed, Exception? failure, string? text)
    {
        if (state.Execution.Snapshot.CanReceive) SetStatus(failure?.Message ?? text ?? "PTT released.");
        return Task.CompletedTask;
    }
    void ITransmitLifecyclePresentation.ClearActivation() { }
    void ITransmitLifecyclePresentation.Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message)
        => Log(timestamp, source, severity, message);
}
