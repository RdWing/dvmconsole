// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using DvmConsole.Media;
using DvmConsole.Operations;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    internal sealed partial class PreparedLiveSession : IReceiveFrameObservationPort,
        IReceiveIngressPresentation, IReceiveMediaPresentation, IReceiveChannelTrafficPort,
        IReceiveCallHistoryPresentation, IReceiveEpisodeRetirementPort, ITransmitLifecyclePresentation
    {
        private DesktopRuntimeDependencies dependencies = null!;
        private IReadOnlyList<ConsoleChannelState> operationalChannels = [];
        public IReadOnlyDictionary<SystemId, ReceiveIngressSystem> IngressSystems { get; private set; } =
            new Dictionary<SystemId, ReceiveIngressSystem>();
        private IClock Clock => SystemClock.Instance;
        private bool IsStopping => Runtime.Admission.IsSuppressed;
        private ConsoleSessionStatus Status => State!.Status;
        private void Persist() { if (Presentation is { } view) view.PersistUserSettings(); else dependencies.UserSettingsStore.Save(Settings); }
        private readonly DesktopSessionAudioPolicy audioPolicy;
        private DesktopSessionAudioPolicy AudioPolicy => audioPolicy;
        private string? OutputDevice(ChannelId id) => AudioPolicy.OutputDevice(Runtime.Channels[id].SettingsKey);
        private DmrReceiveKeyPolicy ReceiveKeyPolicy() => AudioPolicy.ReceiveKeyPolicy;
        private IVocoderBackend CreateReceiveVocoder() => Presentation is { } view ? view.CreateReceiveVocoderBackend()
            : dependencies.VocoderFactory.Create(ConsoleReceiveProcessingProfile.Capture(Settings.RxAudioProcessingOptions));
        private IVocoderBackend CreatePatchVocoder() => DesktopSessionAudioPolicy.CreatePatchVocoder(dependencies.VocoderFactory);
        private ReceiveJitterBufferProfile JitterProfile(ChannelId channel, RadioMediaProtocol protocol)
            => Runtime.Buffering.GetProfile(Runtime.Channels[channel].Runtime.Definition.SystemName, protocol);

        public ConsoleLiveSessionPorts CreatePorts(IReadOnlyList<IRadioSession> radios, DesktopRuntimeDependencies dependencies,
            ITransmitKeyPort keys, bool patchSourceIdPassthrough,
            IEnumerable<GroupConfiguration>? groups = null, string? configurationPath = null,
            IReadOnlyDictionary<SystemId, string>? systemEndpoints = null,
            IReadOnlyList<ConsoleChannelState>? operationalChannels = null)
        {
            this.dependencies = dependencies;
            preparedSystemEndpoints = systemEndpoints ?? new Dictionary<SystemId, string>();
            this.operationalChannels = operationalChannels ?? State!.Topology.Channels
                .Select(channel => Runtime.Channels[channel.Id]).ToArray();
            ConsoleChannelPreferenceRestoration.Apply(this.operationalChannels, Settings);
            InitializeAudioBackend(dependencies);
            InitializeRecordingStore(dependencies);
            playbackStore = new(Recordings.Store);
            var systems = State!.Topology.Systems.Select(system => new ReceiveIngressSystem(system.Id, system.Name,
                State.Topology.Channels.Where(channel => channel.SystemId == system.Id)
                    .Select(channel => State.Channels[channel.Id]).ToArray())).ToArray();
            IngressSystems = systems.ToDictionary(system => system.Id);
            Runtime.Buffering.Apply(AudioPolicy.ReceiveBuffering(systems.Select(system => system.Name)));
            var receive = new ConsoleLiveReceivePorts(
                new(new ReceiveAudioBackendPort(AudioBackendProvider.CreateBackend, CreateReceiveVocoder),
                    new ReceiveAudioRoutePolicy(Runtime.Media.Gain, Runtime.Media.Balance, OutputDevice),
                    new ReceiveAudioKeyPort(keys.P25, keys.Dmr, keys.Nxdn, ReceiveKeyPolicy),
                    new ReceiveAudioPresentationPort(ObserveDecoded, (id, stream, samples, delay) =>
                        ObserveMeter(id, stream, samples.Span, ChannelAudioDirection.Receive, delay))),
                new(this, (id, timing) =>
                {
                    if (Presentation is { } view) { view.ObserveRuntimeReceiveTiming(timing); view.HandleReceiveWorkItemTiming(id, timing); }
                }, JitterProfile, entries => ReportReceiveWorkShutdownDelay("Receive audio", entries)),
                new(this.operationalChannels, State.ReceiveMute, AudioReconfigurationLock, () => IsStopping,
                    id => Runtime.ReceiveDiagnostics.Reset(id), Status.SetAudio,
                    new ReceiveOutputPresentationPort(Settings, Recordings, Runtime.Media,
                        (id, previous, enabled) => Presentation?.ResolveChannel(id).PresentReceiveSelection(previous, enabled, "SetAudioEnabled"),
                        action => { if (Presentation is { } view) return view.RunOnUiThreadAsync(action); action(); return Task.CompletedTask; }, Persist,
                        () => Presentation?.NotifySelectedOutputMutePresentationChanged(), Status.SetAudio),
                    new ReceiveOutputLifetimePort(() => IsStopping, (elapsed, result) => Presentation?.ObserveRouteRecovery(elapsed, result)),
                    (id, error) => Presentation?.ReportRecordingDecodeFailureAsync(id, error) ?? Task.CompletedTask),
                Clock, diagnostic => Presentation?.PublishReceiveDiagnostic(diagnostic));
            var transmit = new ConsoleLiveTransmitPorts(keys, AudioPolicy.Input,
                new TransmitAudioBackendPort(AudioBackendProvider.CreateBackend, CreateReceiveVocoder),
                new TransmitSampleObservationPort(borrowed: ObserveTransmit, fault: failure =>
                {
                    Presentation?.ObserveTransmitHealthError(failure);
                    DesktopCrashLog.Write("Transmit sample observation", failure);
                }), () => Settings.AudioOutputDeviceId, null,
                new TransmitAudioPresentationPort(Runtime.Media, ReceiveSync, null, Log, Status.SetAudio),
                new TransmitAudioGate(AudioReconfigurationLock), this, TransmitAdmissionGate,
                new ManualTransmitPolicy(Runtime.TransmitChannels, Runtime.TransmitTargets.FindSystem, () => IsStopping,
                    () => Presentation?.TalkPermitTone ?? Settings.TalkPermitTone, Status.SetTransmit,
                    ids => { if (Presentation is { } view) view.PresentManualTransmitStarting(ids); else Status.SetTransmit("Starting PTT…"); }, dependencies.NetworkDisabledDemo),
                () => ObjectDisposedException.ThrowIf(IsStopping, this), () => Settings.LocalToneMonitorEnabled,
                new ConsoleManualTransmitObservers(Started: (target, stream, record, diagnostics, elapsed) =>
                    Presentation?.PresentManualTransmitStarted(target, stream, record, diagnostics, elapsed),
                    Completed: (stream, call) => Presentation?.PresentManualTransmitCompleted(stream, call)));
            var patch = new ConsoleLivePatchPorts(radios, keys, CreateReceiveVocoder, CreatePatchVocoder,
                Runtime.TransmitChannels.Find, ReceiveKeyPolicy, patchSourceIdPassthrough,
                diagnostic => Presentation?.HandlePatchForwardingDiagnostic(diagnostic), JitterProfile,
                entries => ReportReceiveWorkShutdownDelay("Patch receive audio", entries),
                error => Status.SetAudio($"Patch source decode stopped: {error.Message}"));
            var traffic = new ConsoleLiveTrafficPorts(systems,
                (id, episode) => Recordings.StopEpisode(Runtime.Media.DescribeRecording(id), episode), this,
                FneReceiveFrameNormalization.Instance, new(this, this, this, this), () => IsStopping, ReceiveSync, State);
            var connections = new ConsoleLiveConnectionPorts(radios.Select(radio => RadioConnectionEndpoint.FromSession(
                radio.SystemId, radio.Name, radio, State.KeyRequests[radio.SystemId])).ToArray(),
                new ConnectionPresentationPort(value => Presentation?.SetBusy(value), Status.SetConsole,
                    id => { if (Presentation is { } view) view.SelectedSystem = view.Systems.Single(system => system.Id == id); },
                    (id, message) =>
                    {
                        if (Presentation is { } view)
                        {
                            SystemViewModel system = view.Systems.Single(candidate => candidate.Id == id);
                            view.HandleSystemStatus(system, new FneConnectionStatus(system.Name,
                                FneConnectionState.Faulted, message, DateTimeOffset.UtcNow));
                        }
                        else Status.SetConsole(message);
                    }),
                SynchronizePatchSourcesAsync, () => IsStopping);
            var transientGroups = new CodeplugGroupState();
            var groupPorts = new ConsoleLivePatchConfigurationPorts(
                () => Presentation?.codeplugGroupState ?? (string.IsNullOrWhiteSpace(configurationPath)
                    ? transientGroups : CodeplugGroupStateStore.GetOrMigrate(Settings, configurationPath)),
                (groups ?? []).Select(group => new PatchGroupRuntimeDefinition(group.Name, group.IsMultiselectGroup())).ToArray(),
                Settings.RetainPatchStateOnStartup);
            return new(new(new(State.ReceiveEpisodes, State.History, Recordings, Runtime.Media.DescribeRecording),
                    receive, transmit, patch, traffic, connections),
                CreateCommandPorts(), new(),
                new(playbackStore, AudioBackendProvider.CreateBackend, () => Settings.AudioOutputDeviceId,
                    failure => Presentation?.HandleRecordingPlaybackFaulted(failure),
                    metrics => Presentation?.HandleRecordingPlaybackStarted(metrics)),
                new(new(AudioBackendProvider.CreateBackend, () => Settings.AudioOutputDeviceId),
                    state => webStateObserver?.Invoke(state) ?? ValueTask.CompletedTask),
                groupPorts, CreateSnapshotPorts(), PrepareLifecycle(radios, keys.P25 as P25KeyRing),
                new(sessionServices!.Connection, "radio-session-ingress", HandleTraffic,
                    HandleAuthorityChanged, HandleConnectionChanged, HandleKeyReceived, HandleSubscriberAcknowledged));
        }

        private Func<WebStreamPlaybackState, ValueTask>? webStateObserver;
        private RecordingPlaybackCoordinator.RecordingPlaybackStoreAdapter playbackStore = null!;

        public void InitializeMedia(IReadOnlyList<IRadioSession> radios, DesktopRuntimeDependencies dependencies,
            ITransmitKeyPort keys, bool patchSourceIdPassthrough,
            IEnumerable<GroupConfiguration>? groups = null, string? configurationPath = null,
            IReadOnlyDictionary<SystemId, string>? systemEndpoints = null,
            IReadOnlyList<ConsoleChannelState>? operationalChannels = null)
        {
            Runtime.BindRadios(radios);
            ConsoleLiveSessionFactory.Initialize(Runtime, CreatePorts(radios, dependencies, keys,
                patchSourceIdPassthrough, groups, configurationPath, systemEndpoints, operationalChannels));
            BindPreparedRuntime(radios, keys);
        }

        public void BindPreparedRuntime(IReadOnlyList<IRadioSession> radios, ITransmitKeyPort keys)
        {
            recordingPlayback = new RecordingPlaybackCoordinator(playbackStore,
                Runtime.RecordingPlayback!, failure => Presentation?.HandleRecordingPlaybackFaulted(failure));
            BindRecordingPlaybackState();
            webPlayback = new WebStreamPlaybackCoordinator(observer =>
                { webStateObserver = observer; return Runtime.WebPlayback!; },
                stream => Settings.WebStreamOutputDeviceIds.GetValueOrDefault(stream.Name), dependencies.UiDispatcher);
            InitializeControlsAndIngress();
            InitializeLifecycleMaintenance();
        }

        private void ObserveDecoded(ChannelId id, uint stream, uint source, ReadOnlyMemory<short> samples)
        {
            if (Presentation is { } view) { view.HandleDecodedSamples(id, stream, source, samples); return; }
            if (!Runtime.Patches.Decoder.IsActive(id)) Runtime.Patches.Forwarding.ObserveDecodedSamples(id, stream, source, samples);
            Runtime.Recording.ObserveDecoded(id, stream, source, samples);
        }
        private void ObserveTransmit(ChannelId id, uint stream, uint source, ReadOnlySpan<short> samples)
        {
            if (Presentation is { } view) view.HandleTransmitSamples(id, stream, source, samples);
            else
            {
                Runtime.TransmitState.ObserveSamples(id, stream, source, samples);
                ObserveMeter(id, stream, samples, ChannelAudioDirection.Transmit);
            }
        }
        public void PublishDiagnostics(ChannelId id, uint stream, DateTimeOffset now) => Runtime.ReceiveDiagnostics.Inspect(id, stream, now);
        public void ShowFault(ChannelId id, Exception failure) { Status.SetAudio(failure.Message); if (Presentation is IReceiveFrameObservationPort view) view.ShowFault(id, failure); }
        public void RecordIngress(ReceiveIngressSystem system, IRadioMediaFrame frame) { if (Presentation is IReceiveIngressPresentation view) view.RecordIngress(system, frame); }
        public void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message) => Presentation?.PostToUi(() => Presentation?.AddDebugLog(timestamp, source, severity, message));
        public void ReportDroppedFrame(ChannelId channel) { }
        public void PlaybackChanged(ChannelId channel) { }
        public void PublishFinalJitterSummary(ChannelId channel, uint stream) { if (Presentation is IReceiveMediaPresentation view) view.PublishFinalJitterSummary(channel, stream); }
        public void ReportCleanupFailure(ChannelId channel, uint stream, Exception failure) => Log(Clock.UtcNow, "RX", DebugLogSeverity.Warning, failure.Message);
        public void Projected(ChannelId channel, ChannelReceiveProjectionResult result) { if (Presentation is IReceiveChannelTrafficPort view) view.Projected(channel, result); }
        public void IgnoredLate(ChannelId channel, uint stream, DateTimeOffset now) { if (Presentation is IReceiveChannelTrafficPort view) view.IgnoredLate(channel, stream, now); }
        public void HistoryChanged() => Presentation?.PostToUi(() => Presentation?.NotifyCallHistoryChanged());
        public void NonCallTerminator(SystemId system) { if (Presentation is IReceiveChannelTrafficPort view) view.NonCallTerminator(system); }
        public string DescribeSignalQuality(IRadioMediaFrame traffic) => traffic is FneTrafficFrame frame ? DescribeFneSignalQuality(frame) : string.Empty;
        public void Project(ConsoleCallHistoryRecord record) { if (Presentation is IReceiveCallHistoryPresentation view) view.Project(record); }
        public void ProjectCompletion(ConsoleCallCompletion completion) { if (Presentation is IReceiveEpisodeRetirementPort view) view.ProjectCompletion(completion); }
        public void ReportCompleted(DateTimeOffset now, string system, string message) => Log(now, system, DebugLogSeverity.Info, message);
        public void ReportFailure(ReceiveCallEpisodeSnapshot episode, Exception failure) => Presentation?.ReportReceiveEpisodeCompletionFailure(episode, failure);
        public DateTimeOffset Now => Clock.UtcNow.ToLocalTime();
        public long GetTimestamp() => Stopwatch.GetTimestamp();
        public TimeSpan GetElapsedTime(long started) => Stopwatch.GetElapsedTime(started);
        public bool MuteReceiveWhileTransmitting => Settings.MuteRxAudioWhileTransmitting;
        public bool? SelectedMicrophoneIsBluetooth => Presentation?.SelectedAudioInputDevice?.IsBluetooth;
        public Task StartedAsync(IReadOnlyList<TransmitTarget> targets, IReadOnlyList<ChannelId> ids, TransmitStartupDiagnostics diagnostics, Func<TimeSpan> elapsed)
        {
            if (Presentation is ITransmitLifecyclePresentation view) return view.StartedAsync(targets, ids, diagnostics, elapsed);
            Status.SetTransmit($"Transmitting on {ids.Count} channel(s).");
            return Task.CompletedTask;
        }
        public Task StartFailedAsync(IReadOnlyList<ChannelId> ids, Exception failure)
        {
            if (Presentation is ITransmitLifecyclePresentation view) return view.StartFailedAsync(ids, failure);
            Status.SetTransmit($"PTT failed: {failure.Message}");
            return Task.CompletedTask;
        }
        public Task StoppingAsync(IReadOnlyList<TransmitStream> streams)
        {
            if (Presentation is ITransmitLifecyclePresentation view) return view.StoppingAsync(streams);
            Status.SetTransmit($"Releasing PTT on {streams.Count} channel(s)…");
            return Task.CompletedTask;
        }
        public Task StoppedAsync(IReadOnlyList<ChannelId> ids, IReadOnlyList<TransmitStream> streams, IReadOnlySet<ChannelId> unresolved, TimeSpan elapsed, Exception? failure, string? status)
        {
            if (Presentation is ITransmitLifecyclePresentation view) return view.StoppedAsync(ids, streams, unresolved, elapsed, failure, status);
            Status.SetTransmit(unresolved.Count > 0 ? $"PTT release failed: {failure?.Message ?? "stop was not confirmed"}"
                : failure is null ? status ?? "PTT idle." : $"Transmission stopped safely after an error: {failure.Message}");
            return Task.CompletedTask;
        }
        public void ClearActivation() => State!.ManualTransmitOwnership.Clear();
    }
}
