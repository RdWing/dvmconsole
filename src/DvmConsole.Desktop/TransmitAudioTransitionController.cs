// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Diagnostics;
using System.Diagnostics;

namespace DvmConsole.Desktop;

/// <summary>
/// Owns the receive-output transition and local permit cue used around
/// transmissions. It deliberately has no knowledge of PTT ownership or the
/// channel-card presentation maintained by the main view-model facade.
/// </summary>
internal sealed class TransmitAudioTransitionController
{
    private readonly ITransmitReceiveRoutePort routes;
    private readonly ITransmitReceiveMutePort mute;
    private readonly ITransmitPermitTonePort tones;
    private readonly ITransmitAudioPresentationPort presentation;
    private readonly ITransmitAudioGate gate;
    private ChannelViewModel[] suspendedChannels = [];
    private bool suspendedChannelsKeptActive;

    public TransmitAudioTransitionController(
        ITransmitReceiveRoutePort routes,
        ITransmitReceiveMutePort mute,
        ITransmitPermitTonePort tones,
        ITransmitAudioPresentationPort presentation,
        ITransmitAudioGate gate)
    {
        this.routes = routes ?? throw new ArgumentNullException(nameof(routes));
        this.mute = mute ?? throw new ArgumentNullException(nameof(mute));
        this.tones = tones ?? throw new ArgumentNullException(nameof(tones));
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public static async Task ObservePreparedPermitToneFailureAsync(
        Task<LocalTonePlaybackResult>? preparedPermitTone)
    {
        if (preparedPermitTone is null || preparedPermitTone.IsCompletedSuccessfully)
            return;

        try
        {
            await preparedPermitTone.ConfigureAwait(false);
        }
        catch
        {
            // The primary startup failure is reported by the caller. Observe a
            // concurrently prepared cue's failure so it cannot escape as an
            // unobserved background exception.
        }
    }

    public async Task EndColdBluetoothReceiveTransitionAsync(long discardedAtStart)
    {
        try
        {
            long discardedAtEnd = routes.SetLivePlaybackDiscarded(discarded: false);
            long discardedSamples = Math.Max(0, discardedAtEnd - discardedAtStart);
            await presentation.RunAsync(() => presentation.Log(
                DateTimeOffset.Now,
                "RX",
                DebugLogSeverity.Debug,
                $"Cold Bluetooth PTT transition discarded " +
                $"{discardedSamples * 1000.0 / PcmAudioFormat.Voice8KhzMono16Bit.SampleRate:0} ms " +
                "of live speaker-bound RX audio; call state and TAR observation continued."))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await presentation.RunAsync(() => presentation.Log(
                DateTimeOffset.Now,
                "RX",
                DebugLogSeverity.Warning,
                $"Unable to release the cold Bluetooth RX transition gate: {exception.Message}"))
                .ConfigureAwait(false);
        }
    }

    public async Task PlayTalkPermitToneAsync(
        bool reportSuccess,
        bool requiredForTransmit = false,
        bool microphoneStartedCold = false,
        bool? microphoneIsBluetooth = false,
        Task<LocalTonePlaybackResult>? preparedPlayback = null,
        Stopwatch? pttTimer = null,
        TimeSpan? transmitSessionsReadyAt = null,
        TimeSpan? cueBarrierReleasedAt = null,
        TimeSpan? microphoneReadyAt = null)
    {
        try
        {
            LocalTonePlaybackResult result = preparedPlayback is not null
                ? await preparedPlayback.ConfigureAwait(false)
                : requiredForTransmit
                    ? await tones.PlayTalkPermitAsync(
                        microphoneStartedCold,
                        microphoneIsBluetooth).ConfigureAwait(false)
                    : await tones.PlayAsync(LocalToneCues.TalkPermit).ConfigureAwait(false);
            string drainText = result.QueuedSamples is int queued &&
                               result.ConsumedSamples is int consumed
                ? $" queued {MainWindowViewModel.FormatAudioLevelDuration(queued)} / " +
                  $"consumed {MainWindowViewModel.FormatAudioLevelDuration(consumed)}"
                : string.Empty;
            LocalTonePresentationEvidence evidence = result.PresentationEvidence;
            string presentationText = evidence.CallbackConsumptionConfirmed
                ? evidence.WarmupCallbacksBefore is long
                    ? $" Output callback consumption confirmed (warm-up {evidence.WarmupCallbacksBefore}->{evidence.WarmupCallbacksAfter}, " +
                      $"cue {evidence.CueCallbacksBefore}->{evidence.CueCallbacksAfter})."
                    : $" Output callback consumption confirmed (cue {evidence.CueCallbacksBefore}->{evidence.CueCallbacksAfter}; no warm-up)."
                : " Output callback confirmation was not requested for this cue.";
            string presentationLatencyText = result.MeasuredOutputPresentationLatency is TimeSpan outputLatency
                ? $" CoreAudio presentation latency {outputLatency.TotalMilliseconds:0} ms; " +
                  $"post-drain wait {result.PostDrainWaitDuration.TotalMilliseconds:0} ms."
                : result.PostDrainWaitDuration > TimeSpan.Zero
                    ? $" Fixed post-drain wait {result.PostDrainWaitDuration.TotalMilliseconds:0} ms."
                    : " No post-drain wait.";
            string pttText = pttTimer is null
                ? string.Empty
                : $" PTT sessions {transmitSessionsReadyAt?.TotalMilliseconds:0} ms, " +
                  $"cue barrier {cueBarrierReleasedAt?.TotalMilliseconds:0} ms, " +
                  $"microphone ready {microphoneReadyAt?.TotalMilliseconds:0} ms, " +
                  $"permit complete {pttTimer.Elapsed.TotalMilliseconds:0} ms (includes tone and post-drain wait).";
            LocalTonePlaybackTiming timing = result.Timing;
            string callbackTiming = timing.CallbackConfirmationObserved is TimeSpan confirmedAt
                ? $"callback confirmation observed {confirmedAt.TotalMilliseconds:0} ms (checked after drain)"
                : "callback confirmation timing unavailable";
            await presentation.RunAsync(() => presentation.Log(
                DateTimeOffset.Now,
                "TX",
                DebugLogSeverity.Debug,
                $"Talk permit tone completed on {result.Output.Name} ({result.Output.Id}) " +
                $"after {result.Attempts} playback attempt(s).{drainText}{pttText}{presentationText}" +
                $"{presentationLatencyText} " +
                $"Output preparation: gate {timing.GateAcquired.TotalMilliseconds:0} ms, " +
                $"initial route {timing.InitialRouteResolved.TotalMilliseconds:0} ms, " +
                $"initial open {timing.InitialPlaybackOpened.TotalMilliseconds:0} ms, " +
                $"cue release {timing.CueReleased.TotalMilliseconds:0} ms, " +
                $"final route {timing.OutputRouteConfirmed.TotalMilliseconds:0} ms, " +
                $"final open {timing.FinalPlaybackOpened.TotalMilliseconds:0} ms, " +
                $"warm-up {timing.OutputWarmupDrained.TotalMilliseconds:0} ms, " +
                $"cue write started {timing.CueWriteStarted.TotalMilliseconds:0} ms, " +
                $"cue queued {timing.CueQueued.TotalMilliseconds:0} ms, " +
                $"audio write {(timing.CueQueued - timing.CueWriteStarted).TotalMilliseconds:0} ms, " +
                $"{callbackTiming}, " +
                $"cue drained {timing.CueDrained.TotalMilliseconds:0} ms, " +
                $"complete {timing.Completed.TotalMilliseconds:0} ms."))
                .ConfigureAwait(false);
            if (reportSuccess)
            {
                await presentation.RunAsync(() =>
                    presentation.PublishStatus(
                        $"Talk permit tone sent to {result.Output.Name}.{drainText}"))
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await presentation.RunAsync(() =>
            {
                presentation.Log(
                    DateTimeOffset.Now,
                    "TX",
                    DebugLogSeverity.Warning,
                    $"Talk permit tone unavailable: {exception}");
                presentation.PublishStatus($"Talk permit tone unavailable: {exception.Message}");
            }).ConfigureAwait(false);
            if (requiredForTransmit)
            {
                throw new InvalidOperationException(
                    "The talk-permit tone could not be completed, so microphone audio remained muted.",
                    exception);
            }
        }
    }

    public Task RestoreSuspendedAudioAsync()
        => gate.RunAsync(RestoreSuspendedAudioCoreAsync);

    private async Task RestoreSuspendedAudioCoreAsync()
    {
        ChannelViewModel[] channels = suspendedChannels;
        bool keptActive = suspendedChannelsKeptActive;
        var remaining = new List<ChannelViewModel>(channels);
        var failures = new List<Exception>();
        foreach (ChannelViewModel channel in channels)
        {
            if (!channel.IsAudioSuspended)
            {
                remaining.Remove(channel);
                continue;
            }

            try
            {
                if (keptActive && routes.IsActive(channel.Id))
                {
                    await routes
                        .SetGainAsync(channel.Id, presentation.GetVolume(channel))
                        .ConfigureAwait(false);
                    bool enableLivePlayback = mute.ShouldEnableLivePlayback(
                        channel,
                        isTemporarilySuspended: false);
                    await routes.SetLivePlaybackEnabledAsync(channel.Id, enableLivePlayback)
                        .ConfigureAwait(false);
                    await presentation.RunAsync(() => channel.SetAudioSuspended(false))
                        .ConfigureAwait(false);
                }
                else
                {
                    // A selection can be cleared while a tone is sending.
                    // Restoration must not turn it back on.
                    if (!channel.IsAudioEnabled)
                    {
                        await presentation.RunAsync(() => channel.SetAudioSuspended(false)).ConfigureAwait(false);
                        remaining.Remove(channel);
                        continue;
                    }
                    await routes.StartAsync(channel).ConfigureAwait(false);
                    if (!routes.IsActive(channel.Id))
                    {
                        throw new InvalidOperationException(
                            $"Receive audio for {channel.Name} could not be restored.");
                    }
                    await presentation.RunAsync(() => channel.SetAudioSuspended(false))
                        .ConfigureAwait(false);
                }
                remaining.Remove(channel);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        suspendedChannels = remaining.ToArray();
        if (remaining.Count == 0)
            suspendedChannelsKeptActive = false;

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Receive audio could not be fully restored.", failures);
    }

    public async Task MuteReceiveAudioAsync(string statusText)
    {
        await gate.RunAsync(async () =>
        {
            ChannelViewModel[] receivingChannels = presentation.Resolve(routes.LivePlaybackChannels);
            if (receivingChannels.Length == 0)
                return;

            var suspended = new List<ChannelViewModel>(receivingChannels.Length);
            var failures = new List<Exception>();
            foreach (ChannelViewModel receivingChannel in receivingChannels)
            {
                try
                {
                    await routes
                        .SetLivePlaybackEnabledAsync(receivingChannel.Id, enabled: false)
                        .ConfigureAwait(false);
                    suspended.Add(receivingChannel);
                    await presentation.RunAsync(() => receivingChannel.SetAudioSuspended(true))
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(new InvalidOperationException(
                        $"Receive output for {receivingChannel.Name} could not be muted.",
                        exception));
                }
            }

            suspendedChannels = suspendedChannels.Concat(suspended).Distinct().ToArray();
            suspendedChannelsKeptActive = suspendedChannels.Length > 0;

            if (failures.Count > 0)
            {
                try
                {
                    await RestoreSuspendedAudioCoreAsync().ConfigureAwait(false);
                }
                catch (Exception rollbackFailure)
                {
                    // Failed restorations remain tracked for the next release/recovery.
                    failures.Add(rollbackFailure);
                }
            }
            if (failures.Count == 1)
                throw failures[0];
            if (failures.Count > 1)
                throw new AggregateException("Receive audio could not be safely muted.", failures);

            await presentation.RunAsync(() => presentation.PublishStatus(statusText))
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
