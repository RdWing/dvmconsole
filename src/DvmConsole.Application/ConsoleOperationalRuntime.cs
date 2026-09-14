// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Immutable;
using DvmConsole.Media;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

/// <summary>
/// Constructs one session's operational graph. Host adapters configure endpoints;
/// every component joins the same ordered session registry, including rollback.
/// Registration stages preserve interleaving with host-owned resources.
/// </summary>
internal sealed partial class ConsoleOperationalRuntime
{
    private readonly ConsoleSessionServices services;
    private int registered;
    private int compositionStarted;
    public bool IsComposed { get; private set; }

    internal void BeginComposition()
    {
        if (Interlocked.Exchange(ref compositionStarted, 1) != 0)
            throw new InvalidOperationException("A live runtime can only be composed once.");
    }

    internal void CompleteComposition()
    {
        // Validate the required operational capabilities before attaching ingress.
        _ = Connections; _ = TransmitTargets; _ = ReceiveDiagnostics;
        _ = EpisodeRetirement; _ = Traffic; _ = TransmitState;
        _ = Commands; _ = TransmitControls; _ = RecordingControls; _ = AudioSettings;
        IsComposed = true;
    }
    private ReceiveCallEpisodeTracker episodes = null!;
    private ConsoleCallHistory history = null!;
    private IRadioSession[] radios = [];
    private readonly object snapshotSync = new();
    private ConsoleSnapshotState? snapshots;
    private ConsoleSnapshotContextObserver? snapshotContext;
    private bool snapshotOwnershipRegistered;

    public static ConsoleOperationalRuntime Prepare(ConsoleSessionServices services, ConsoleSessionState state,
        IP25KeyResolver? p25 = null, IDmrKeyResolver? dmr = null, INxdnKeyResolver? nxdn = null)
        => new(services, state.CreateSnapshotChannels(p25, dmr, nxdn), state.Media, state.Terminal);

    public ConsoleOperationalRuntime(ConsoleSessionServices services,
        IEnumerable<ConsoleSnapshotChannel> channels, ConsoleChannelMediaDirectory? media = null,
        SessionTerminalFence? terminal = null)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        Admission = new(terminal ?? new SessionTerminalFence());
        var metadata = channels.ToImmutableArray();
        SnapshotChannels = metadata;
        Channels = metadata.ToFrozenDictionary(channel => channel.State.Id, channel => channel.State);
        Media = media ?? new(metadata.Select(channel => (channel.State, channel.Aliases)));
        TransmitChannels = new(metadata.Select(channel => (channel.State, channel.Access)));
        Authority = new(metadata.Select(channel => channel.State));
        Recording = new(metadata.Select(channel => channel.State));
    }

    public RecordingPlaybackChannelState RecordingPlaybackState { get; } = new();
    public ConsoleSessionAdmission Admission { get; }
    public ImmutableArray<ConsoleSnapshotChannel> SnapshotChannels { get; }
    public IReadOnlyDictionary<ChannelId, ConsoleChannelState> Channels { get; }

    public ConsoleSnapshotState GetOrCreateSnapshots(ConsoleTopologySnapshot topology,
        ConsoleSnapshotContextSource context, ConsoleSessionStatus status)
    {
        lock (snapshotSync)
        {
            RegisterSnapshotOwnership();
            return snapshots ??= new ConsoleSnapshotState(topology, SnapshotChannels, context.Capture, status);
        }
    }
    public void RegisterSnapshotOwnership()
    {
        lock (snapshotSync)
        {
            if (snapshotOwnershipRegistered) return;
            services.Presentation.Register("operational-snapshots", () =>
            {
                snapshots?.Dispose();
                return ValueTask.CompletedTask;
            });
            services.Presentation.Register("snapshot-context", () =>
            {
                snapshotContext?.Dispose();
                return ValueTask.CompletedTask;
            });
            snapshotOwnershipRegistered = true;
        }
    }

    public void ObserveSnapshotContext(ReceiveMuteState mute, RecordingPlaybackChannelState playback,
        IReceiveRecordingSession? recordings, P25KeyRetrievalCoordinator? keys = null)
    {
        ConsoleSnapshotContextObserver observer;
        lock (snapshotSync)
        {
            if (snapshotContext is null)
                Register(128, () => snapshotContext = new ConsoleSnapshotContextObserver(
                    snapshots ?? throw new InvalidOperationException("Initialize snapshots before observing their context."),
                    mute, playback, recordings, keys));
            observer = snapshotContext!;
        }
        if (keys is not null) observer.BindKeys(keys);
    }

    public ConsoleChannelMediaDirectory Media { get; }
    public ConsoleTransmitChannelDirectory TransmitChannels { get; }
    public TalkgroupAuthorityController Authority { get; }
    public ConsoleReceiveRuntime Receive { get; } = new();
    public ConsoleTransmitRuntime Transmit { get; } = new();
    public ConsolePatchRuntime Patches { get; } = new();
    public ConsoleRecordingRuntime Recording { get; }
    private ConnectionSessionController? connections;
    public ConnectionSessionController Connections
    {
        get => connections ?? throw new InvalidOperationException("Initialize Connections before using this runtime component.");
        private set => connections = value;
    }
    private TransmitTargetResolver? transmitTargets;
    public TransmitTargetResolver TransmitTargets
    {
        get => transmitTargets ?? throw new InvalidOperationException("Initialize TransmitTargets before using this runtime component.");
        private set => transmitTargets = value;
    }
    private RadioSessionIngressCoordinator? radioIngress;
    public RadioSessionIngressCoordinator RadioIngress
    {
        get => radioIngress ?? throw new InvalidOperationException("Initialize RadioIngress before using this runtime component.");
        private set => radioIngress = value;
    }
    private ConsoleReceiveDiagnostics? receiveDiagnostics;
    public ConsoleReceiveDiagnostics ReceiveDiagnostics
    {
        get => receiveDiagnostics ?? throw new InvalidOperationException("Initialize ReceiveDiagnostics before using this runtime component.");
        private set => receiveDiagnostics = value;
    }
    private ReceiveEpisodeRetirement? episodeRetirement;
    public ReceiveEpisodeRetirement EpisodeRetirement
    {
        get => episodeRetirement ?? throw new InvalidOperationException("Initialize EpisodeRetirement before using this runtime component.");
        private set => episodeRetirement = value;
    }
    private ReceiveTrafficRuntime? traffic;
    public ReceiveTrafficRuntime Traffic
    {
        get => traffic ?? throw new InvalidOperationException("Initialize Traffic before using this runtime component.");
        private set => traffic = value;
    }
    private ConsoleTransmitState? transmitState;
    public ConsoleTransmitState TransmitState
    {
        get => transmitState ?? throw new InvalidOperationException("Initialize TransmitState before using this runtime component.");
        private set => transmitState = value;
    }

    public void InitializeRecording(ReceiveCallEpisodeTracker episodes, ConsoleCallHistory history,
        IReceiveRecordingSink? capture, Func<ChannelId, ChannelRecordingDescriptor> describe,
        Action? historyChanged = null, object? recordingSynchronization = null, Func<bool>? isRecordingStopped = null)
    {
        if (transmitState is not null) throw new InvalidOperationException("Recording state is already initialized.");
        this.episodes = episodes;
        this.history = history;
        Recording.Initialize(Channels, episodes, history, capture, describe, historyChanged,
            recordingSynchronization, isRecordingStopped ?? (() => Admission.IsSuppressed));
        TransmitState = new(Media, history, capture as ITransmitRecordingSink);
    }

    public void InitializeConnections(IReadOnlyList<RadioConnectionEndpoint> endpoints,
        IConnectionPatchLifecyclePort patches, IConnectionPresentationPort presentation, Func<bool> isStopping)
    {
        if (connections is not null) throw new InvalidOperationException("Connection controls are already initialized.");
        Connections = new(endpoints, patches, presentation, new ConnectionAdmissionPort(isStopping));
    }

    public void BindRadios(IEnumerable<IRadioSession> sessions)
    {
        if (transmitTargets is not null) throw new InvalidOperationException("Radio bindings are already initialized.");
        radios = sessions.ToArray();
        TransmitTargets = new(TransmitChannels, radios);
    }

    public void InitializeRadioIngress(ConsoleSessionServiceScope scope, string name,
        Action<RadioSessionIngressCoordinator>? detach = null)
    {
        if (radioIngress is not null) throw new InvalidOperationException("Radio ingress is already initialized.");
        if (transmitTargets is null) throw new InvalidOperationException("Bind radios before creating ingress.");
        scope.Register(name, () =>
        {
            if (radioIngress is not null)
            {
                try { detach?.Invoke(RadioIngress); }
                finally { RadioIngress.Dispose(); }
            }
            return ValueTask.CompletedTask;
        });
        RadioIngress = new(radios);
    }

    public void InitializeReceive(ReceiveRuntimeAudioPorts audio, ReceiveRuntimeWorkPorts work,
        ReceiveRuntimeControlPorts control, IClock clock, Action<ConsoleReceiveDiagnostic> publish)
    {
        Receive.Initialize(audio, work, control, clock, Recording);
        ReceiveDiagnostics = new(Media, Receive.Audio, Receive.Work, publish);
    }

    public void InitializeReceiveEpisodes(IReadOnlyList<ReceiveIngressSystem> systems,
        Action<ChannelId, long> stopRecording, IReceiveEpisodeRetirementPort presentation)
    {
        if (episodeRetirement is not null) throw new InvalidOperationException("Receive episodes are already initialized.");
        if (transmitState is null) throw new InvalidOperationException("Initialize recording before receive episodes.");
        EpisodeRetirement = Receive.ConfigureEpisodes(episodes, systems, Recording.Targets, stopRecording, history, presentation);
    }

    public void InitializeReceiveTraffic(IReadOnlyList<ReceiveIngressSystem> systems,
        IRadioReceiveFrameNormalizer normalizer, ReceiveTrafficPresentationPorts presentation,
        Func<bool> isStopping, object receiveSynchronization, ConsoleSessionState? sharedState = null,
        Func<ChannelId, IRadioMediaFrame, long?, bool>? enqueuePatch = null)
    {
        if (traffic is not null) throw new InvalidOperationException("Receive traffic is already initialized.");
        if (episodeRetirement is null || Patches.Forwarding is null)
            throw new InvalidOperationException("Initialize receive episodes and patches before traffic.");
        var media = new ReceiveMediaPort(Receive.Audio, Receive.Work, Patches.Decoder,
            Patches.Work, Patches.Forwarding, isStopping, enqueuePatch);
        Traffic = new(systems, episodes, media, EpisodeRetirement, history, Media, Admission, Buffering, receiveSynchronization, normalizer, presentation, sharedState);
    }

    public void RegisterReceiveAudioOwnership()
        => Register(1, () => Receive.RegisterAudioOwnership(services));
    public void RegisterReceiveWorkOwnership()
        => Register(2, () => Receive.RegisterWorkOwnership(services));
    public void RegisterReceiveOutputOwnership()
        => Register(4, () => Receive.RegisterOutputOwnership(services));
    public void RegisterPatchOwnership(Action? detachPresentation = null)
        => Register(8, () => Patches.RegisterOwnership(services, detachPresentation));
    public void RegisterRecordingOwnership(ConsoleSessionServiceScope scope)
        => Register(16, () => scope.Own("recording-history-observer", Recording));
    public void RegisterTransmitOwnership(string name, SemaphoreSlim commandGate, SemaphoreSlim admissionGate,
        Func<Task> prepareMicrophoneRetirement, Action? beforeRetirement = null)
        => Register(32, () => Transmit.RegisterCoordinatorOwnership(services, name, commandGate, admissionGate,
            prepareMicrophoneRetirement, beforeRetirement));

    private void Register(int component, Action register)
    {
        if ((Interlocked.Or(ref registered, component) & component) != 0)
            throw new InvalidOperationException("Operational component ownership is already registered.");
        register();
    }
}
