// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Operations;

namespace DvmConsole.Application;

public sealed record ConsoleReceiveSessionDependencies(ConsoleHostServices Host,
    IReadOnlyList<RadioSystemDescriptor> Systems, IRadioReceiveFrameNormalizer Normalizer,
    IP25KeyResolver? P25 = null, IDmrKeyResolver? Dmr = null, INxdnKeyResolver? Nxdn = null, TimeProvider? TimeProvider = null, string? InitialStatus = null, IReceiveRecordingSession? Recordings = null, IConsoleReceivePreferences? Preferences = null, AudioInputProcessingOptions? ManualInput = null);

/// <summary>Shared live console composition with host-selected transmit capability.</summary>
public sealed partial class ConsoleReceiveSession : IConsoleSessionRuntimeAdapter, IConsoleCommands, IConsoleSessionReactivation, IConsoleSessionInputAdmission, IConsoleConnectionCommands, IConsoleRecordingCommands, IConsoleHistoryCommands, IConsoleConnectionStateNotifications
{
    private readonly ConsoleSessionState state;
    private readonly ConsoleSessionServices services;
    private readonly TimeProvider time;
    private readonly ConsoleReceiveSessionDependencies dependencies;
    private readonly ConsoleOperationalRuntime operationalRuntime;
    private ConsoleReceiveRuntime receive => operationalRuntime.Receive;
    private readonly ChannelAudioCommandQueue audioCommands;
    private ConsolePatchRuntime patches => operationalRuntime.Patches;
    private Task patchPause = Task.CompletedTask;
    private readonly SemaphoreSlim reconfiguration = new(1, 1);
    private readonly object ingressSync = new();
    private ConsoleSnapshotState snapshots = null!;
    private readonly ConsoleTopologySnapshot topology;
    private ConsoleRadioSessions radios = null!;
    private ReceiveIngressCoordinator ingress => operationalRuntime.Traffic?.Ingress!;
    private ReceiveEpisodeRetirement retirement => operationalRuntime.EpisodeRetirement;
    private IReadOnlyDictionary<SystemId, ReceiveIngressSystem> systems = null!;
    // Each adapter captures its transport state atomically. Reading presentation facts
    // must not start, stop, or reconcile operational services.
    public ImmutableArray<RadioConnectionSnapshot> ConnectionStates => radios.Sessions.Values
        .OfType<IRadioConnectionStateSource>().Select(source => source.ConnectionState).ToImmutableArray();
    public event EventHandler? ConnectionStatesChanged;

    private void OnConnectionStateChanged(object? sender, RadioConnectionSnapshot snapshot)
        => radioLifecycle.OnConnection(sender, snapshot);

    private bool audioUnavailable;
    private long audioGeneration;
    private readonly AsyncDisposal disposal = new();
    private ConsoleTransmitChannelDirectory transmitChannels => operationalRuntime.TransmitChannels;
    private TalkgroupAuthorityController talkgroupAuthority => operationalRuntime.Authority;
    private ConsoleTransmitState transmitState => operationalRuntime.TransmitState;
    private TransmitTargetResolver transmitTargets => operationalRuntime.TransmitTargets;
    private ConsoleReceiveDiagnostics receiveDiagnostics => operationalRuntime.ReceiveDiagnostics;
    private readonly SemaphoreSlim commands = new(1, 1);
    private CancellationTokenSource lifetime = new();
    private readonly object connectionIntentSync = new();
    private CancellationTokenSource connectionIntent = new();
    private int disposalStarted;
    private int listeningActivated;
    private ConnectionSessionController connections => operationalRuntime.Connections;

    private ConsoleReceiveSession(ConsoleLiveSessionPreparation preparation,
        ConsoleReceiveSessionDependencies dependencies)
    {
        state = preparation.State;
        services = preparation.Services;
        radios = preparation.Radios;
        operationalRuntime = preparation.Runtime;
        audioCommands = new(RunCommandAsync, () => operationalRuntime.AudioSettings,
            dependencies.TimeProvider ?? TimeProvider.System, dependencies.Host.Delay);
        this.dependencies = dependencies;
        runtimeHealth = new(dependencies.Recordings as IRecordingFinalizationHealthSource);
        subscriberCommands = new(dependencies.Host.Clock);
        subscriberCommands.HistoryChanged += OnSubscriberHistoryChanged;
        time = dependencies.TimeProvider ?? TimeProvider.System;
        operationalRuntime.RegisterSnapshotOwnership();
        state.Status.SetConsole(dependencies.InitialStatus ?? (dependencies.ManualInput is null
            ? "Receive-only session ready. Connections are idle." : "Session ready. Connections are idle."));
        services.Connection.Own("command-gate", commands);
        services.Connection.Own("command-cancellation", lifetime);
        services.Transmit.Register("tone-preparation", async () =>
        {
            await CancelTonesAsync().ConfigureAwait(false);
            tonePreparation.Dispose();
            tonePreparationGate.Dispose();
        });
        // Capability restriction affects presentation only; imported definitions remain intact.
        topology = dependencies.ManualInput is not null ? state.Topology : state.Topology with { Channels = state.Topology.Channels.Select(channel => channel with { ReceiveOnly = true }).ToImmutableArray() };
    }

    private ConsoleLiveSnapshotPorts PrepareSnapshots()
    {
        var context = new ConsoleSnapshotContextSource(dependencies.Recordings,
            (id, muted) => receive.Output.GetEffectiveMuteReason(id, muted),
            () => OutputMuted, () => recordingPlaybackState.Snapshot.Channel, () => Volatile.Read(ref groupMemberships),
            allowTransmitControls: dependencies.ManualInput is not null);
        return new(topology, context, state.Status, state.ReceiveMute, dependencies.Recordings);
    }

    private void AttachSnapshots()
    {
        snapshots = operationalRuntime.Snapshots;
        snapshots.Changed += HandleSnapshotChanged;
        services.Presentation.Register("session-snapshots", () =>
        {
            snapshots.Changed -= HandleSnapshotChanged;
            return ValueTask.CompletedTask;
        });
    }

    public static async ValueTask<ConsoleReceiveSession> CreateAsync(ConsoleConfiguration configuration,
        Func<ConsoleSessionState, ConsoleSessionServices, ConsoleReceiveSessionDependencies> prepareHost,
        ConfigurationReference? reference = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepareHost);
        ConsoleReceiveSessionDependencies dependencies = null!;
        WebStreamPlaybackDescriptor[] definitions = [];
        PatchGroupRuntimeDefinition[] groups = [];
        bool patchSourceIdPassthrough = false;
        var request = new ConsoleLiveSessionRequest<ConsoleReceiveSession>(configuration, reference, null,
            (state, services) =>
            {
                definitions = configuration.Zones.SelectMany(zone => zone.WebStreams ?? []).Select(stream =>
                    new WebStreamPlaybackDescriptor(WebStreamId.FromIdentity(stream.Name, stream.Url), stream.Name,
                        stream.Url, stream.AuthUsername ?? string.Empty, stream.AuthPassword ?? string.Empty, 1, null)).ToArray();
                groups = configuration.EffectiveGroups()
                    .Select(group => new PatchGroupRuntimeDefinition(group.Name, group.IsMultiselectGroup())).ToArray();
                patchSourceIdPassthrough = configuration.PatchSourceIdPassthrough;
                dependencies = prepareHost(state, services);
                return new Keys(dependencies);
            },
            (_, _) => new ConsoleRadioSessionPlan(dependencies.Systems.Select(system =>
                new ConsoleRadioSessionBinding(system, dependencies.Host.RadioSessions))),
            (preparation, _) =>
            {
                var session = new ConsoleReceiveSession(preparation, dependencies);
                return ValueTask.FromResult(new ConsoleLiveHostPreparation<ConsoleReceiveSession>(session,
                    session.PreparePorts(patchSourceIdPassthrough, definitions, groups)));
            });
        var prepared = await ConsoleLiveSessionFactory.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return await ConsoleSessionConstruction.CreateAsync(prepared.Services, prepared.State.Terminal, async token =>
        {
            var session = prepared.Host;
            session.AttachRuntime();
            await session.webStreams.PrepareAsync(token).ConfigureAwait(false);
            await session.RestorePreferencesAsync(token).ConfigureAwait(false);
            return session;
        }, cancellationToken).ConfigureAwait(false);
    }

    private ConsoleLiveSessionPorts PreparePorts(bool patchSourceIdPassthrough,
        IReadOnlyList<WebStreamPlaybackDescriptor> webDefinitions, IReadOnlyList<PatchGroupRuntimeDefinition> groupDefinitions)
    {
        var host = dependencies.Host;
        InitializeReceiveBuffering();
        InitializeConnectionCues();
        InitializeP25KeyRetrieval();
        var recordingPorts = PrepareRecording();
        services.Audio.Own("reconfiguration-lock", reconfiguration);
        operationalRuntime.RegisterReceiveAudioOwnership();
        operationalRuntime.RegisterReceiveWorkOwnership();
        operationalRuntime.RegisterReceiveOutputOwnership();
        var receivePorts = new ConsoleLiveReceivePorts(new(new ReceiveAudioBackendPort(
                () => host.AudioBackends.Create(AudioBackendConfiguration.Default), () => host.Vocoders.Create(Volatile.Read(ref receiveProcessing))),
            new ReceiveAudioRoutePolicy(state.Media.Gain, state.Media.Balance),
            new ReceiveAudioKeyPort(dependencies.P25, dependencies.Dmr, dependencies.Nxdn, GetDmrReceiveKeyPolicy), new ReceiveAudioPresentationPort(decoded: recordingRuntime.ObserveDecoded, presented: ObservePresentedSamples)),
            new(this, (channel, timing) =>
            {
                runtimeHealth.ObserveReceiveTiming(timing);
                receiveDiagnostics.ObserveTiming(channel, timing, host.Clock.UtcNow);
            }, GetReceiveBufferingProfile, _ => { }),
            new(state.Channels.Values.ToArray(), state.ReceiveMute, reconfiguration, () => IsStopping,
                channel => receiveDiagnostics.Reset(channel), SetStatus, this, this, (_, exception) => { SetStatus(exception.Message); return Task.CompletedTask; }), host.Clock, diagnostic =>
        {
            Log(diagnostic.Timestamp, "RX", diagnostic.Severity, diagnostic.Message);
            if (diagnostic.ShowStatus) SetStatus(diagnostic.Message);
        });
        var playbackPorts = PrepareRecordingPlayback();
        var transmitPorts = PrepareManualTransmit();
        operationalRuntime.RegisterPatchOwnership();
        var patchPorts = new ConsoleLivePatchPorts(radios.Sessions.Values, new Keys(dependencies), () => host.Vocoders.Create(),
            () => host.Vocoders.Create(ConsoleReceiveProcessingProfile.PatchSource),
            transmitChannels.Find,
            GetDmrReceiveKeyPolicy, patchSourceIdPassthrough, ObservePatchDiagnostic,
            GetReceiveBufferingProfile, _ => { }, exception => ReportMediaFailure("PATCH", exception));
        systems = state.Topology.Systems.ToDictionary(system => system.Id, system => new ReceiveIngressSystem(system.Id, system.Name,
            state.Topology.Channels.Where(channel => channel.SystemId == system.Id).Select(channel => state.Channels[channel.Id]).ToArray()));
        var trafficPorts = new ConsoleLiveTrafficPorts(systems.Values.ToArray(),
            (id, episodeId) => dependencies.Recordings?.StopEpisode(DescribeRecording(id), episodeId), this,
            dependencies.Normalizer, new ReceiveTrafficPresentationPorts(this, this, this, this),
            () => IsStopping, ingressSync, state,
            (channel, frame, timestamp) => Volatile.Read(ref radioTransitions) == 0 && patches.Enqueue(channel, frame, timestamp));
        var connectionPorts = new ConsoleLiveConnectionPorts(radios.Sessions.Values.Select(radio =>
            RadioConnectionEndpoint.FromSession(radio.SystemId, radio.Name, radio, state.KeyRequests[radio.SystemId])).ToArray(),
            new ConnectionPresentationPort(_ => { }, SetStatus, _ => { },
                (id, message) => SetStatus($"{radios.Sessions[id].Name}: {message}")), _ => Task.CompletedTask, () => IsStopping);
        var commandPorts = new ConsoleLiveChannelCommandPorts(
            new(id => CanSelectTransmit(state.Channels[id]), SaveTransmitPreferenceAsync,
                result => { if (result.Applied) Changed(result.Channel); }),
            new(CanRecord, (id, enabled, token) => SavePreferenceAsync(id, new(RecordingEnabled: enabled), token),
                action => { lock (ingressSync) action(); }, ReconcileRecordingSelectionAsync, id => Changed(id)),
            new(SavePreferenceAsync, id => Changed(id)), () => IsStopping);
        return new(new(recordingPorts, receivePorts, transmitPorts, patchPorts, trafficPorts, connectionPorts),
            commandPorts, new(ApplyReceiveMeter, ApplyTransmitMeter, time), playbackPorts,
            PrepareWebStreams(webDefinitions), new(() => groupSettings, groupDefinitions, false,
                Port: this, ApplyRestoration: false), PrepareSnapshots(), PrepareRadioLifecycle(),
            new(services.Presentation, "radio-ingress", OnTraffic, OnAuthority, OnConnectionStateChanged,
                OnP25KeyReceived, OnSubscriberAcknowledged, OnRadioLog));
    }

    private void AttachRuntime()
    {
        var host = dependencies.Host;
        AttachSnapshots();
        AttachRecordingPlayback();
        AttachManualTransmit();
        services.Timers.OwnAsync("receive-lifecycle", host.Scheduler.CreatePeriodic(TimeSpan.FromMilliseconds(100), token =>
        {
            lock (ingressSync)
            {
                if (!IsStopping)
                {
                    ingress.Advance(host.Clock.UtcNow);
                    if (retirement.Advance(host.Clock.UtcNow)) Changed();
                }
            }
            return ValueTask.CompletedTask;
        }));
        meterWork = services.Timers.OwnAsync("receive-meters", host.Scheduler.CreatePeriodic(
            TimeSpan.FromMilliseconds(ChannelAudioMeterPipeline.RefreshIntervalMilliseconds), token =>
            { AdvanceMeters(); return ValueTask.CompletedTask; }, startImmediately: false));
        services.Timers.OwnAsync("receive-recovery", host.Scheduler.CreatePeriodic(TimeSpan.FromSeconds(1),
            token =>
            {
                subscriberCommands.Expire();
                // Restored operator intent is not permission for a prepared
                // replacement to acquire the outgoing session's audio output.
                return Volatile.Read(ref listeningActivated) == 0 || IsStopping || !state.Execution.Snapshot.CanReceive
                    ? ValueTask.CompletedTask : new ValueTask(receive.Sessions.ReconcileAsync(token));
            }));
    }

    public IConsoleCommands Commands => this;
    public ConsoleExecutionPolicy Execution => state.Execution;
    public bool IsAcceptingCommands => !IsStopping;
    public IReadOnlyList<ConsoleCallHistoryRecord> History => state.History.Snapshot;
    public event EventHandler? ControlStateInvalidated;
    public event EventHandler<ChannelMeterSample>? MeterSampled;
    public event EventHandler<ConsoleLogEvent>? LogPublished;
    public ConsoleTopologySnapshot CaptureTopology() => topology;
    public ConsoleRuntimeSnapshot CaptureSnapshot() { lock (ingressSync) return snapshots.Capture(); }
    public ConsoleSnapshotUpdate CaptureUpdate(ConsoleRuntimeSnapshot previous) { lock (ingressSync) return snapshots.CaptureUpdate(previous); }
    private RadioAliasIndex AliasesFor(ConsoleChannelState channel)
        => state.Aliases[SystemId.FromName(channel.Runtime.Definition.SystemName)];
    private bool IsStopping => operationalRuntime.Admission.IsSuppressed;
    private void Changed(ChannelId? id = null)
    {
        if (IsStopping) return;
        snapshots?.Invalidate(id);
        PublishControlChange();
    }
    private void SetStatus(string value)
    {
        if (IsStopping) return;
        state.Status.SetConsole(value);
    }
    private void HandleSnapshotChanged(object? sender, EventArgs args)
    {
        if (!IsStopping) PublishControlChange();
    }
    private void PublishControlChange()
    {
        foreach (EventHandler observer in ControlStateInvalidated?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* A presentation observer cannot interrupt receive processing. */ }
        }
    }
    private void OnTraffic(object? sender, RadioTrafficRecord record)
    {
        lock (ingressSync) if (!IsStopping && systems.TryGetValue(record.SystemId, out var system)) ingress.HandleIngress(system, record);
    }
    private void OnAuthority(object? sender, TalkgroupAuthorityRecord record)
    {
        // Manual ownership and authority admission share the mobile ingress boundary.
        lock (ingressSync) radioLifecycle.ApplyAuthority(record);
    }
    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        => RunConnectionCommandAsync(connections.ConnectAsync, cancellationToken);

    public Task ResumeAudioAsync(Func<CancellationToken, Task> restore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restore);
        long generation;
        // Intent belongs to the request, not to whenever the command queue
        // reaches it. A later pause invalidates even a not-yet-started recovery.
        lock (ingressSync) generation = audioGeneration;
        return RunCommandAsync(async token =>
        {
            try
            {
                Task patchCleanup;
                Task webCleanup;
                lock (ingressSync)
                {
                    if (generation != audioGeneration || IsStopping)
                        throw new OperationCanceledException("A newer audio interruption superseded queued recovery.");
                    patchCleanup = patchPause;
                    webCleanup = webPause;
                }
                // Teardown can release the last physical output and change the native
                // audio generation. Finish it before beginning native restoration.
                await patchCleanup.WaitAsync(token).ConfigureAwait(false);
                await webCleanup.WaitAsync(token).ConfigureAwait(false);
                Task restoration;
                lock (ingressSync)
                {
                    token.ThrowIfCancellationRequested();
                    if (generation != audioGeneration || IsStopping)
                        throw new OperationCanceledException("A newer audio interruption superseded queued recovery.");
                    restoration = restore(token);
                }
                await restoration.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (ingressSync)
                {
                    // A failed native completion may belong to an older interruption.
                    // Only the current recovery owns the operational failure state.
                    if (generation == audioGeneration && !IsStopping && !token.IsCancellationRequested)
                        SetAudioAvailable(false, $"Listening recovery failed: {exception.Message} Open Settings to resume.");
                }
                throw;
            }
            token.ThrowIfCancellationRequested();
            Task webRecovery;
            lock (ingressSync)
            {
                if (generation != audioGeneration || IsStopping)
                    throw new OperationCanceledException("A newer audio interruption superseded recovery.");
                SetAudioAvailable(true, "Listening resumed. Interrupted transmissions remain stopped.");
                webRecovery = webStreams.ResumeAsync(token);
            }
            await webRecovery.ConfigureAwait(false);
        }, cancellationToken).AsTask();
    }

    public void SetAudioAvailable(bool available, string reason, bool requiresExplicitResume = false)
    {
        lock (ingressSync)
        {
            if (IsStopping) return;
            if (available) patches.Resume(CaptureCurrentPatchStreams());
            if (!available)
            {
                connectionCues?.Cancel();
                patchPause = patches.PauseAsync();
                webPause = webStreams.PauseAsync();
                DvmConsole.Threading.TaskObservation.Observe(webPause);
                DvmConsole.Threading.TaskObservation.Observe(patchPause);
                CancelManualStartup();
                DvmConsole.Threading.TaskObservation.Observe(CancelTonesAsync());
                RequestManualRelease();
                audioGeneration++;
                recordingStartup?.Cancel();
                recordingPlayback?.RequestStop();
            }
            audioUnavailable = !available;
            if (!available) ResetMeters();
            receive.Audio.SetLivePlaybackDiscarded(!available);
            if (!available)
            {
                if (requiresExplicitResume) state.Execution.MediaServicesReset();
                else state.Execution.Interrupt();
            }
            else if (state.Execution.BeginRecovery(explicitResume: true) is { } recovery)
                state.Execution.CompleteRecovery(recovery, succeeded: true);
            SetStatus(reason);
        }
    }

    public IReadOnlyList<SystemId> CaptureActiveSystemIds()
        => connections.CaptureActiveSystemIds();

    Task IConsoleConnectionCommands.ConnectAsync(CancellationToken cancellationToken) => ConnectAsync(cancellationToken).AsTask();
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource outgoing;
        lock (connectionIntentSync)
        {
            ObjectDisposedException.ThrowIf(IsStopping, this);
            cancellationToken.ThrowIfCancellationRequested();
            outgoing = connectionIntent;
            connectionIntent = new CancellationTokenSource();
        }
        // Cancel active and queued startup intent before waiting for command ownership.
        using (outgoing) outgoing.Cancel();
        return RunRadioTransitionAsync(connections.DisconnectAsync, cancellationToken).AsTask();
    }
    public Task ToggleAsync(SystemId id, CancellationToken cancellationToken = default)
        => RunConnectionCommandAsync(token => connections.ToggleAsync(id, token), cancellationToken).AsTask();
    public async Task RestoreAsync(IEnumerable<SystemId> ids, CancellationToken cancellationToken = default)
    {
        await RunConnectionCommandAsync(token => connections.RestoreAsync(ids, token), cancellationToken).ConfigureAwait(false);
        await ResumeWebStreamsAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SuspendInput()
    {
        lock (ingressSync)
        {
            operationalRuntime.Admission.Suspend();
            RequestManualRelease();
        }
    }

    public async ValueTask QuiesceAsync(CancellationToken cancellationToken)
    {
        lock (ingressSync)
        {
            if (!IsStopping) ResetMeters();
            patchPause = patches.PauseAsync();
            webPause = webStreams.PauseAsync();
            CancelManualStartup();
            DvmConsole.Threading.TaskObservation.Observe(CancelTonesAsync());
            operationalRuntime.Admission.Suspend();
            recordingPlayback?.RequestStop();
            state.Execution.Interrupt();
            receive.Audio.SetLivePlaybackDiscarded(true);
        }
        PublishControlChange();
        subscriberCommands.Interrupt();
        p25KeyRetrieval?.Pause();
        lifetime.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        // Join delayed slider work before rollback can reopen command admission.
        await audioCommands.FlushAsync(deadline.Token).ConfigureAwait(false);
        await commands.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            await CancelTonesAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
            await patchPause.WaitAsync(deadline.Token).ConfigureAwait(false);
            await webPause.WaitAsync(deadline.Token).ConfigureAwait(false);
            await ReleaseManualAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
            if (recordingPlayback is not null) await recordingPlayback.StopAsync(deadline.Token).ConfigureAwait(false);
            await connections.DisconnectAsync(deadline.Token).ConfigureAwait(false);
            foreach (var id in state.Channels.Keys) await receive.Work.StopAsync(id).WaitAsync(deadline.Token).ConfigureAwait(false);
            await receive.Audio.StopAsync(deadline.Token).ConfigureAwait(false);
            if (dependencies.Recordings is { } recordings)
            {
                foreach (var id in state.Channels.Keys) recordings.StopChannel(DescribeRecording(id));
                await recordings.DrainAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        finally { commands.Release(); }
    }

    public void ReactivateAfterFailedReplacement()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
        if (!commands.Wait(0)) throw new InvalidOperationException("Receive commands are still stopping.");
        try
        {
            if (!IsStopping) return;
            lifetime = new CancellationTokenSource();
            services.Connection.Own("restored-command-cancellation", lifetime);
            // Replacement rollback restores ownership, not permission to resume
            // audio that was already interrupted or explicitly paused.
            if (!audioUnavailable && state.Execution.BeginRecovery(explicitResume: true) is { } recovery)
                state.Execution.CompleteRecovery(recovery, succeeded: true);
            receive.Audio.SetLivePlaybackDiscarded(audioUnavailable);
            if (!operationalRuntime.Admission.TryResume())
                throw new ObjectDisposedException(nameof(ConsoleReceiveSession));
        }
        finally { commands.Release(); }
    }

    private async ValueTask RunConnectionCommandAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        CancellationTokenSource intent;
        lock (connectionIntentSync)
        {
            ObjectDisposedException.ThrowIf(IsStopping, this);
            intent = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionIntent.Token);
        }
        using (intent)
            await RunRadioTransitionAsync(action, intent.Token).ConfigureAwait(false);
    }

    private async ValueTask RunRadioTransitionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref radioTransitions);
        try
        {
            // Revoke input before waiting for the connection command queue.
            // Keep admission closed until transport changes have completed.
            lock (ingressSync) patchPause = patches.PauseAsync();
            await patchPause.ConfigureAwait(false);
            await CancelTonesAsync().ConfigureAwait(false);
            await ReleaseManualAsync().ConfigureAwait(false);
            await RunCommandAsync(async token =>
            {
                try { await action(token).ConfigureAwait(false); }
                finally { await RefreshLivePatchesAsync(CancellationToken.None).ConfigureAwait(false); }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref radioTransitions); }
    }

    private async ValueTask RunCommandAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsStopping, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await commands.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsStopping, this);
            await action(linked.Token).ConfigureAwait(false);
        }
        finally { commands.Release(); }
    }
    public async ValueTask FlushSettingsAsync(CancellationToken cancellationToken)
    {
        await audioCommands.FlushAsync(cancellationToken).ConfigureAwait(false);
        // Each preference command awaits its atomic write. Joining the gate also
        // joins an in-flight save before the host prepares a replacement.
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        commands.Release();
        await webStreams.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() => disposal.RunAsync(async () =>
    {
        Interlocked.Exchange(ref disposalStarted, 1);
        state.Execution.Stop();
        try { await QuiesceAsync(CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            operationalRuntime.Admission.Close();
            lock (connectionIntentSync) connectionIntent.Dispose();
            await services.DisposeAsync().ConfigureAwait(false);
        }
    });
    public ValueTask ClearSessionHistoryAsync(CancellationToken cancellationToken = default)
        => RunCommandAsync(_ =>
        {
            lock (ingressSync) state.History.Clear();
            SetStatus("Session history cleared. TAR recordings are retained.");
            return Task.CompletedTask;
        }, cancellationToken);

    public ValueTask SetReceiveEnabledAsync(ChannelId id, bool enabled, CancellationToken cancellationToken = default)
        => RunCommandAsync(token => receive.Output.SetSelectionAsync([id], enabled,
            (channel, selected, saveToken) => SavePreferenceAsync(channel, new(ReceiveEnabled: selected), saveToken).AsTask(),
            cancellationToken: token), cancellationToken);
    public ValueTask SetChannelGainAsync(ChannelId id, double value, CancellationToken cancellationToken = default)
        => audioCommands.SetAsync(id, value, balance: false, cancellationToken);

    public ValueTask SetChannelBalanceAsync(ChannelId id, double value, CancellationToken cancellationToken = default)
        => audioCommands.SetAsync(id, value, balance: true, cancellationToken);

    private sealed record Keys(ConsoleReceiveSessionDependencies Dependencies) : ITransmitKeyPort
    { public IP25KeyResolver? P25 => Dependencies.P25; public IDmrKeyResolver? Dmr => Dependencies.Dmr; public INxdnKeyResolver? Nxdn => Dependencies.Nxdn; }
}
