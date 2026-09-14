// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Application;

internal sealed record ConsoleLiveMediaPorts(ConsoleLiveRecordingPorts Recording,
    ConsoleLiveReceivePorts Receive, ConsoleLiveTransmitPorts? Transmit, ConsoleLivePatchPorts Patches,
    ConsoleLiveTrafficPorts Traffic, ConsoleLiveConnectionPorts Connections);

internal sealed record ConsoleLiveTransmitCommandPorts(
    Func<ChannelId, bool> CanSelect,
    Func<ChannelId, ChannelTransmitPreferenceChange, CancellationToken, ValueTask> Save,
    Action<ChannelSelectionResult>? Changed = null);

internal sealed record ConsoleLiveRecordingCommandPorts(
    Func<ChannelId, bool> CanRecord,
    Func<ChannelId, bool, CancellationToken, ValueTask> Save,
    Action<Action> ApplyState,
    Func<ChannelId, bool, CancellationToken, Task> Reconcile,
    Action<ChannelId>? Changed = null);

internal sealed record ConsoleLiveAudioCommandPorts(
    Func<ChannelId, ChannelReceivePreferenceChange, CancellationToken, ValueTask> Save,
    Action<ChannelId>? Changed = null);

internal sealed record ConsoleLiveChannelCommandPorts(
    ConsoleLiveTransmitCommandPorts Transmit,
    ConsoleLiveRecordingCommandPorts Recording,
    ConsoleLiveAudioCommandPorts Audio,
    Func<bool> IsStopping);

internal sealed record ConsoleLiveMeterPorts(Action<ChannelAudioMeterUpdate>? Receive = null,
    Action<ChannelAudioMeterUpdate>? Transmit = null, TimeProvider? TimeProvider = null);

internal sealed record ConsoleLiveRecordingPlaybackPorts(IRecordingStore Store,
    Func<IAudioBackend> CreateBackend, Func<string?> OutputDevice,
    Action<Exception>? Fault = null, Action<RecordingPlaybackStartupMetrics>? Started = null,
    Func<CancellationToken, ValueTask<IAudioPlayback>>? OpenSharedOutput = null);

internal sealed record ConsoleLiveWebPlaybackPorts(ConsoleWebPlaybackDependencies Dependencies,
    Func<WebStreamPlaybackState, ValueTask>? Observe = null,
    IReadOnlyList<WebStreamPlaybackDescriptor>? SessionDefinitions = null,
    IConsoleWebStreamPreferences? SessionPreferences = null);

internal sealed record ConsoleLivePatchConfigurationPorts(Func<CodeplugGroupState> Settings,
    IReadOnlyList<PatchGroupRuntimeDefinition> Definitions, bool RestoreEnabled,
    IPatchConfigurationPort? Port = null, bool ApplyRestoration = true);

internal sealed record ConsoleLiveSnapshotPorts(ConsoleTopologySnapshot Topology,
    ConsoleSnapshotContextSource Context, ConsoleSessionStatus Status,
    ReceiveMuteState Mute, IReceiveRecordingSession? Recordings = null);

internal sealed record ConsoleLiveRadioLifecyclePorts(ConsoleSessionState State,
    IReadOnlyDictionary<SystemId, IRadioSession> Radios, P25KeyRetrievalCoordinator? Keys,
    IReadOnlyDictionary<SystemId, P25KeyRequestPort> KeyRequests,
    ConsoleSubscriberCommandDispatcher Subscribers, IConsoleRadioLifecyclePort Host);

internal sealed record ConsoleLiveIngressPorts(ConsoleSessionServiceScope Scope, string Name,
    EventHandler<RadioTrafficRecord>? Traffic = null,
    EventHandler<TalkgroupAuthorityRecord>? Authority = null,
    EventHandler<RadioConnectionSnapshot>? Connection = null,
    EventHandler<RadioP25KeyResponse>? Key = null,
    EventHandler<ConsoleSubscriberAcknowledgement>? Subscriber = null,
    EventHandler<DebugLogEntry>? Log = null);

/// <summary>Host-created resources and narrow policy/presentation ports; no runtime initialization callbacks.</summary>
internal sealed record ConsoleLiveSessionPorts(ConsoleLiveMediaPorts Media,
    ConsoleLiveChannelCommandPorts Commands, ConsoleLiveMeterPorts Meters,
    ConsoleLiveRecordingPlaybackPorts? RecordingPlayback,
    ConsoleLiveWebPlaybackPorts? WebPlayback, ConsoleLivePatchConfigurationPorts? Groups,
    ConsoleLiveSnapshotPorts Snapshots, ConsoleLiveRadioLifecyclePorts? Lifecycle = null,
    ConsoleLiveIngressPorts? Ingress = null);

internal sealed record ConsoleLiveSessionPreparation(ConsoleSessionState State,
    ConsoleOperationalRuntime Runtime, ConsoleSessionServices Services, ConsoleRadioSessions Radios,
    ITransmitKeyPort Keys);

internal sealed record ConsoleLiveHostPreparation<T>(T Host, ConsoleLiveSessionPorts Ports);

internal sealed record ConsoleLiveSessionRequest<T>(ConsoleConfiguration Configuration,
    ConfigurationReference? Reference, IReadOnlyCollection<string>? CallPrioritySystems,
    Func<ConsoleSessionState, ConsoleSessionServices, ITransmitKeyPort> PrepareKeys,
    Func<ConsoleSessionState, ITransmitKeyPort, ConsoleRadioSessionPlan> RadioPlan,
    Func<ConsoleLiveSessionPreparation, CancellationToken, ValueTask<ConsoleLiveHostPreparation<T>>> PrepareHost,
    Action<ConsoleSessionServiceDisposalTiming>? ObserveDisposal = null);

internal sealed record ConsolePreparedLiveSession<T>(ConsoleSessionState State,
    ConsoleOperationalRuntime Runtime, ConsoleSessionServices Services,
    ConsoleRadioSessions Radios, T Host);

/// <summary>Validates and assembles one live session before either host constructs presentation.</summary>
internal static class ConsoleLiveSessionFactory
{
    public static ValueTask<ConsolePreparedLiveSession<T>> CreateAsync<T>(ConsoleLiveSessionRequest<T> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        // Validation precedes host storage, key, socket or audio construction.
        ConsoleSessionState state = ConsoleSessionState.Create(request.Configuration, request.Reference,
            request.CallPrioritySystems);
        var services = new ConsoleSessionServices(request.ObserveDisposal);
        return ConsoleSessionConstruction.CreateAsync(services, state.Terminal, async token =>
        {
            ITransmitKeyPort keys = request.PrepareKeys(state, services);
            ConsoleOperationalRuntime runtime = ConsoleOperationalRuntime.Prepare(services, state, keys.P25, keys.Dmr, keys.Nxdn);
            ConsoleRadioSessions radios = await ConsoleRadioSessions.CreateAsync(state, request.RadioPlan(state, keys), token)
                .ConfigureAwait(false);
            services.Connection.OwnAsync("radio-sessions", radios);
            runtime.BindRadios(radios.Sessions.Values);
            ConsoleLiveHostPreparation<T> host = await request.PrepareHost(new(state, runtime, services, radios, keys), token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Initialize(runtime, host.Ports);
            token.ThrowIfCancellationRequested();
            return new ConsolePreparedLiveSession<T>(state, runtime, services, radios, host.Host);
        }, cancellationToken);
    }

    public static void Initialize(ConsoleOperationalRuntime runtime, ConsoleLiveSessionPorts ports)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(ports);
        runtime.BeginComposition();
        var media = ports.Media;
        ConsoleLiveMediaFactory.Initialize(runtime, media.Recording, media.Receive, media.Transmit,
            media.Patches, media.Traffic, media.Connections);
        var commands = ports.Commands;
        runtime.InitializeTransmitControls(commands.Transmit.CanSelect, commands.Transmit.Save,
            commands.IsStopping, commands.Transmit.Changed);
        runtime.InitializeRecordingControls(commands.Recording.CanRecord, commands.Recording.Save,
            commands.Recording.ApplyState, commands.Recording.Reconcile, commands.IsStopping, commands.Recording.Changed);
        runtime.InitializeAudioSettings(commands.Audio.Save, commands.IsStopping, commands.Audio.Changed);
        if (ports.Meters.Receive is { } receive && ports.Meters.Transmit is { } transmit)
            runtime.InitializeMeters(receive, transmit, ports.Meters.TimeProvider);
        else runtime.InitializePresentedMeters();
        if (ports.RecordingPlayback is { } playback)
            runtime.InitializeRecordingPlayback(playback.Store, playback.CreateBackend, playback.OutputDevice,
                playback.Fault, playback.Started, playback.OpenSharedOutput);
        if (ports.WebPlayback is { } web)
        {
            if (web.SessionDefinitions is { } definitions)
                runtime.InitializeWebSession(definitions, web.SessionPreferences, web.Dependencies);
            else
                runtime.InitializeWebPlayback(web.Dependencies, web.Observe ?? (_ => ValueTask.CompletedTask));
        }
        if (ports.Groups is { } groups)
            runtime.InitializePatchConfiguration(groups);
        var snapshots = ports.Snapshots;
        var snapshotState = runtime.GetOrCreateSnapshots(snapshots.Topology, snapshots.Context, snapshots.Status);
        runtime.ObservePatchSnapshotContext(snapshotState);
        runtime.ObserveSnapshotContext(snapshots.Mute, runtime.RecordingPlaybackState, snapshots.Recordings,
            ports.Lifecycle?.Keys);
        if (ports.Lifecycle is { } lifecycle) runtime.InitializeRadioLifecycle(lifecycle);
        runtime.CompleteComposition();
        if (ports.Ingress is { } ingress) runtime.AttachRadioIngress(ingress);
    }
}
