// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

internal sealed record ConsoleLiveRecordingPorts(
    ReceiveCallEpisodeTracker Episodes, ConsoleCallHistory History, IReceiveRecordingSink? Capture,
    Func<ChannelId, ChannelRecordingDescriptor> Describe, Action? HistoryChanged = null);

internal sealed record ConsoleManualTransmitObservers(
    Action<ChannelId, bool>? Changed = null,
    Action<TransmitTarget, uint, ConsoleCallHistoryRecord, TransmitStartupDiagnostics, Func<TimeSpan>>? Started = null,
    Action<TransmitStream, CallId?>? Completed = null);

internal sealed record ConsoleLiveReceivePorts(
    ReceiveRuntimeAudioPorts Audio, ReceiveRuntimeWorkPorts Work, ReceiveRuntimeControlPorts Control,
    IClock Clock, Action<ConsoleReceiveDiagnostic> PublishDiagnostic);

internal sealed record ConsoleLiveTransmitPorts(
    ITransmitKeyPort Keys, AudioInputProcessingOptions Input, ITransmitAudioBackendPort Backend,
    ITransmitSampleObservationPort Samples, Func<string?> OutputRoute,
    Action<ChannelId, bool>? ToneStateChanged,
    ITransmitAudioPresentationPort Presentation, ITransmitAudioGate AudioGate,
    ITransmitLifecyclePresentation Lifecycle, SemaphoreSlim Admission,
    ManualTransmitPolicy Manual, Action ValidateGeneratedAdmission, Func<bool> MonitorEnabled,
    ConsoleManualTransmitObservers Observers, bool UseSharedCueOutput = false);

internal sealed record ConsoleLivePatchPorts(
    IEnumerable<IRadioTrafficEndpoint> Systems, ITransmitKeyPort Keys,
    Func<IVocoderBackend> CreateTransmitVocoder, Func<IVocoderBackend> CreateSourceVocoder,
    Func<ChannelId, TransmitChannelDescriptor?> ResolveChannel,
    Func<DmrReceiveKeyPolicy> ReceiveKeyPolicy, bool SourceIdPassthrough,
    Action<PatchForwardingDiagnostic> ObserveDiagnostic,
    Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile> JitterProfile,
    Action<IReadOnlyList<ReceiveWorkerShutdownDiagnostic>> ObserveShutdown,
    Action<Exception> ReportDecodeFailure);

internal sealed record ConsoleLiveTrafficPorts(
    IReadOnlyList<ReceiveIngressSystem> Systems, Action<ChannelId, long> StopRecording,
    IReceiveEpisodeRetirementPort Episodes, IRadioReceiveFrameNormalizer Normalizer,
    ReceiveTrafficPresentationPorts Presentation, Func<bool> IsStopping, object Synchronization,
    ConsoleSessionState? State = null, Func<ChannelId, IRadioMediaFrame, long?, bool>? EnqueuePatch = null);

internal sealed record ConsoleLiveConnectionPorts(
    IReadOnlyList<RadioConnectionEndpoint> Endpoints, IConnectionPresentationPort Presentation,
    Func<CancellationToken, Task> SynchronizePatches, Func<bool> IsStopping);

/// <summary>
/// The shared assembly path for a live media graph. Hosts prepare endpoints, policy and
/// observers; coordinator construction and dependency order belong here. Ownership slots
/// are reserved before entry in the session's single registry so partial failures unwind
/// through the established host resource order.
/// </summary>
internal static class ConsoleLiveMediaFactory
{
    public static void Initialize(ConsoleOperationalRuntime runtime, ConsoleLiveRecordingPorts recording,
        ConsoleLiveReceivePorts receive,
        ConsoleLiveTransmitPorts? transmit, ConsoleLivePatchPorts patch, ConsoleLiveTrafficPorts traffic,
        ConsoleLiveConnectionPorts connections)
    {
        runtime.InitializeRecording(recording.Episodes, recording.History, recording.Capture,
            recording.Describe, recording.HistoryChanged, traffic.Synchronization);
        runtime.InitializeReceive(receive.Audio, receive.Work, receive.Control, receive.Clock, receive.PublishDiagnostic);
        runtime.InitializeReceiveEpisodes(traffic.Systems, traffic.StopRecording, traffic.Episodes);
        if (transmit is not null)
        {
            var tx = runtime.Transmit;
            tx.InitializeMicrophone(transmit.Keys, transmit.Input, transmit.Backend, transmit.Samples);
            tx.InitializeTones(transmit.OutputRoute, (channel, enabled, stream) =>
            {
                lock (traffic.Synchronization)
                {
                    runtime.Channels[channel].SetTransmitEnabled(enabled, stream);
                    transmit.ToneStateChanged?.Invoke(channel, enabled);
                }
                return ValueTask.CompletedTask;
            }, transmit.UseSharedCueOutput
                ? token => runtime.Receive.Audio.OpenMonitorOutputAsync(cancellationToken: token) : null);
            tx.InitializeTransitions(runtime.Receive.Audio,
                new TransmitReceiveRoutePort(runtime.Receive.Audio, runtime.Receive.Output),
                new ReceiveOutputMutePort(receive.Control.Mute, id => runtime.Channels[id].Operator.Snapshot),
                transmit.Presentation, transmit.AudioGate, transmit.Lifecycle, transmit.Admission,
                transmit.Manual, transmit.ValidateGeneratedAdmission, transmit.MonitorEnabled,
                new ManualTransmitStatePorts(runtime.TransmitState,
                    action => { lock (traffic.Synchronization) action(); },
                    transmit.Observers.Changed, transmit.Observers.Started, transmit.Observers.Completed));
        }
        runtime.Patches.Initialize(patch.Systems, patch.Keys, patch.CreateTransmitVocoder, patch.CreateSourceVocoder,
            patch.ResolveChannel, patch.ReceiveKeyPolicy, patch.SourceIdPassthrough, patch.ObserveDiagnostic,
            patch.JitterProfile, patch.ObserveShutdown, patch.ReportDecodeFailure);
        runtime.InitializeReceiveTraffic(traffic.Systems, traffic.Normalizer, traffic.Presentation,
            traffic.IsStopping, traffic.Synchronization, traffic.State, traffic.EnqueuePatch);
        runtime.InitializeConnections(connections.Endpoints,
            new ConnectionPatchLifecyclePort(connections.SynchronizePatches,
                token => runtime.Patches.Decoder.StopAllAsync(token), runtime.Patches.Forwarding.StopAll),
            connections.Presentation, connections.IsStopping);
    }
}
