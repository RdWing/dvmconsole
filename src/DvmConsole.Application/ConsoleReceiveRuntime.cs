// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Operations;

namespace DvmConsole.Application;

internal sealed record ReceiveRuntimeAudioPorts(
    IReceiveAudioBackendPort Backend,
    IReceiveAudioRoutePolicy Routes,
    IReceiveAudioKeyPort Keys,
    IReceiveAudioPresentationPort Presentation);

internal sealed record ReceiveRuntimeWorkPorts(
    IReceiveFrameObservationPort Observation,
    Action<ChannelId, ReceiveWorkItemTiming> ObserveTiming,
    Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile> GetJitterProfile,
    Action<IReadOnlyList<ReceiveWorkerShutdownDiagnostic>> ObserveShutdownDelay);

internal sealed record ReceiveRuntimeControlPorts(
    IReadOnlyList<ConsoleChannelState> Channels,
    ReceiveMuteState Mute,
    SemaphoreSlim ReconfigurationGate,
    Func<bool> IsStopping,
    Action<ChannelId> ResetDiagnostics,
    Action<string> PublishStatus,
    IReceiveOutputView Presentation,
    IReceiveOutputLifetimePort Lifetime,
    Func<ChannelId, Exception, Task> ReportRecordingFailure);

/// <summary>
/// Constructs the shared receive graph. Ownership hooks are registered before
/// initialization so partial construction follows the session's existing ordered
/// rollback, including when another subsystem fails later in construction.
/// </summary>
internal sealed class ConsoleReceiveRuntime : IReceiveFrameAudioPort
{
    private IReadOnlyDictionary<ChannelId, ConsoleChannelState> channels = null!;
    private ReceiveRuntimeControlPorts control = null!;
    public ChannelReceiveAudioCoordinator Audio { get; private set; } = null!;
    public ChannelReceiveWorkQueue Work { get; private set; } = null!;
    public ReceiveSessionController Sessions { get; private set; } = null!;
    public ReceiveOutputController Output { get; private set; } = null!;

    public void Initialize(ReceiveRuntimeAudioPorts audio, ReceiveRuntimeWorkPorts work,
        ReceiveRuntimeControlPorts control, IClock clock, IReceiveRecordingTrafficPort recording)
    {
        if (Audio is not null)
            throw new InvalidOperationException("Receive runtime has already been initialized.");
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(clock);
        channels = control.Channels.ToDictionary(channel => channel.Id);
        this.control = control;
        Audio = new ChannelReceiveAudioCoordinator(audio.Backend, audio.Routes, audio.Keys, audio.Presentation);
        var processing = new ReceiveFrameProcessingCoordinator(this, work.Observation, clock, recording);
        Work = ChannelReceiveWorkQueue.CreateWithIngressTiming(
            (channel, ingress, cancellation) => processing.ProcessAsync(channel, ingress.Traffic, ingress.Encryption, cancellation),
            timingObserver: work.ObserveTiming,
            getJitterBufferProfile: work.GetJitterProfile,
            shutdownDelayObserver: work.ObserveShutdownDelay);
        Sessions = new ReceiveSessionController(new ReceiveSessionPort(Audio, Work,
            control.Mute, control.ReconfigurationGate, () => control.Channels,
            control.IsStopping, control.ResetDiagnostics, control.PublishStatus, clock));
        Output = new ReceiveOutputController(new ReceiveOutputRoutePort(Audio, Work, Sessions,
            control.ReconfigurationGate, control.ResetDiagnostics, id => channels[id].Runtime.Definition.Name),
            new ReceiveOutputMutePort(control.Mute, id => channels[id].Operator.Snapshot),
            new ReceiveOutputStatePort(channels, control.Presentation), control.Lifetime);
        Audio.OutputFailed += Output.HandleOutputFailed;
    }

    public ReceiveEpisodeRetirement ConfigureEpisodes(ReceiveCallEpisodeTracker episodes,
        IReadOnlyList<ReceiveIngressSystem> systems, ReceiveRecordingTargetIndex recordingTargets,
        Action<ChannelId, long> stopRecording, ConsoleCallHistory history, IReceiveEpisodeRetirementPort presentation)
    {
        if (Audio is null) throw new InvalidOperationException("Initialize receive audio before configuring episodes.");
        Audio.SetReceivePlaybackEpisodeResolver((id, frame) =>
            ReceivePlaybackEpisodeResolver.Resolve(channels[id], episodes, frame));
        var completion = new ReceiveEpisodeCompletionCoordinator(new ReceiveEpisodeCompletionPort(
            Work, Audio, stopRecording, channels, recordingTargets.Resolve));
        return new ReceiveEpisodeRetirement(episodes, completion, new ReceiveEpisodeTargetIndex(systems), history, presentation);
    }

    void IReceiveFrameAudioPort.EndStream(ChannelId channel, uint streamId)
        => channels[channel].EndReceivePlayback(streamId);

    bool IReceiveFrameAudioPort.IsRecordingEnabled(ChannelId channel) => channels[channel].Operator.Snapshot.RecordingEnabled;
    bool IReceiveFrameAudioPort.IsActive(ChannelId channel) => Audio.IsActive(channel);
    Task<ReceiveAudioProcessTiming> IReceiveFrameAudioPort.ProcessAsync(ChannelId channel,
        IRadioMediaFrame traffic, RadioFrameEncryption? encryption, CancellationToken cancellationToken)
        => Audio.ProcessWithTimingAsync(channel, traffic, encryption, cancellationToken);
    void IReceiveFrameAudioPort.RequestRecovery(ChannelId channel, Exception failure)
        => Output.RequestRecovery(channel, failure);
    Task IReceiveFrameAudioPort.StopAsync(ChannelId channel, CancellationToken cancellationToken)
        => Sessions.RetireFailedSessionAsync(channel, cancellationToken);
    bool IReceiveFrameAudioPort.IsDeviceFailure(Exception exception) => IsAudioDeviceFailure(exception);

    public async Task EnsureRecordingAudioAsync(ChannelId channel, CancellationToken cancellationToken = default)
    {
        if (Audio.IsActive(channel) || Sessions.IsRetryPending(channel)) return;
        ConsoleChannelState state = channels[channel];
        try
        {
            ChannelOperatorSnapshot operation = state.Operator.Snapshot;
            // TAR needs decoded PCM even when local playback is muted.
            await Audio.EnsureDecodeAsync(new ReceiveChannelDescriptor(channel, state.Runtime.Definition),
                livePlaybackEnabledWhenCreated: control.Mute.ShouldEnableLivePlayback(channel,
                    operation.AudioEnabled, operation.AudioSuspended), cancellationToken: cancellationToken).ConfigureAwait(false);
            Work.Start(channel);
            control.ResetDiagnostics(channel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Sessions.RecordFailure(channel);
            await control.ReportRecordingFailure(channel, exception).ConfigureAwait(false);
        }
    }

    /// <summary>Rebuilds decoder instances after settings that also affect patch decoding change.</summary>
    public async Task RebuildDecodersAsync(Func<Task>? rebuildPatchDecoders = null,
        Func<bool>? canRestoreListening = null, CancellationToken cancellationToken = default)
    {
        if (control.IsStopping()) return;
        await control.ReconfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ChannelId[] active = Audio.ActiveChannels.ToArray();
            if (active.Length > 0)
            {
                foreach (ChannelId id in active)
                    await Work.StopAsync(id).ConfigureAwait(false);
                await Audio.StopAsync(cancellationToken).ConfigureAwait(false);
                foreach (ChannelId id in active)
                {
                    if (canRestoreListening is not null && !canRestoreListening()) break;
                    ChannelOperatorSnapshot operation = channels[id].Operator.Snapshot;
                    if (operation.AudioEnabled)
                        await Output.StartAsync(id, persistSelection: false, cancellationToken).ConfigureAwait(false);
                    else if (operation.RecordingEnabled)
                        await EnsureRecordingAudioAsync(id, cancellationToken).ConfigureAwait(false);
                }
            }
            if (rebuildPatchDecoders is not null) await rebuildPatchDecoders().ConfigureAwait(false);
        }
        finally { control.ReconfigurationGate.Release(); }
    }

    /// <summary>Recovers each active physical output once while preserving current listening intent.</summary>
    public async Task RecoverActiveOutputsAsync(Func<bool> canReceive, CancellationToken cancellationToken = default)
    {
        var remaining = Audio.ActiveChannels.ToHashSet();
        while (remaining.Count > 0 && canReceive())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChannelId first = remaining.First();
            var result = await Output.RecoverSelectedAsync(first, cancellationToken: cancellationToken).ConfigureAwait(false);
            remaining.Remove(first);
            remaining.ExceptWith(result.Restarted);
            remaining.ExceptWith(result.Failed);
            if (result.Failed.Count > 0)
                throw new IOException("Some receive channels could not restart. Resume listening to retry.");
        }
    }

    private static bool IsAudioDeviceFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException or ObjectDisposedException) return true;
            if (current is InvalidOperationException &&
                (current.Message.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("playback", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("stream", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    public void RegisterAudioOwnership(ConsoleSessionServices services)
    {
        services.Audio.Register("receive-audio-coordinator", () => Audio is null ? ValueTask.CompletedTask : Audio.DisposeAsync());
        services.Audio.Register("receive-output-failure-subscription", () =>
        {
            if (Audio is not null && Output is not null) Audio.OutputFailed -= Output.HandleOutputFailed;
            return ValueTask.CompletedTask;
        });
    }

    public void RegisterWorkOwnership(ConsoleSessionServices services)
        => services.Receive.Register("audio-work", () => Work is null ? ValueTask.CompletedTask : Work.DisposeAsync());

    public void RegisterOutputOwnership(ConsoleSessionServices services)
        => services.Audio.Register("receive-output-controller", () => Output is null ? ValueTask.CompletedTask : Output.DisposeAsync());
}
