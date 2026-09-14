// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Application;

internal readonly record struct ConsoleReceiveDiagnostic(
    DateTimeOffset Timestamp, DebugLogSeverity Severity, string Message, bool ShowStatus = false);

/// <summary>Shared, rate-limited receive diagnostics over session state and media queues.</summary>
internal sealed class ConsoleReceiveDiagnostics(
    ConsoleChannelMediaDirectory channels,
    ChannelReceiveAudioCoordinator audio,
    ChannelReceiveWorkQueue work,
    Action<ConsoleReceiveDiagnostic> publish)
{
    private readonly ReceiveDiagnosticsReporter warnings = new(TimeSpan.FromSeconds(5));
    private readonly ReceivePipelineTimingReporter timing = new(TimeSpan.FromSeconds(5));
    private readonly ReceiveJitterEventReporter jitter = new(TimeSpan.FromSeconds(5));

    public void Inspect(ChannelId channel, uint streamId, DateTimeOffset now)
    {
        if (!warnings.ShouldInspect(channel, now)) return;
        var statistics = audio.GetDiagnostics(channel);
        var state = channels.State(channel).Receive;
        var warning = new ReceiveWarningDiagnostics(statistics.LostPackets, statistics.DuplicateOrLatePackets,
            state.DroppedFrames, state.IgnoredLatePackets, statistics.MalformedPackets);
        if (!warnings.ShouldPublish(channel, warning, now)) return;
        publish(new(now, DebugLogSeverity.Warning,
            ReceiveDiagnosticsText.FormatWarning(channels.State(channel).Runtime.Definition.Name, streamId, warning,
                audio.IsLivePlaybackEnabled(channel), audio.GetPlaybackDiagnostics(channel),
                work.GetDiagnostics(channel, streamId), audio.GetPlaybackArbitrationDiagnostics(channel)), true));
    }

    public void ObserveTiming(ChannelId channel, ReceiveWorkItemTiming sample, DateTimeOffset now)
    {
        PublishJitter(channel, jitter.Observe(channel, sample, now), now);
        if (timing.ShouldPublish(channel, sample, now))
            publish(new(now, DebugLogSeverity.Warning, ReceiveDiagnosticsText.FormatPipelineDelay(
                channels.State(channel).Runtime.Definition.Name, sample, work.GetDiagnostics(channel, sample.Traffic.StreamId))));
    }

    public void Complete(ChannelId channel, uint streamId, DateTimeOffset now)
        => PublishJitter(channel, jitter.Complete(channel, streamId), now);

    public void Reset(ChannelId channel)
    {
        timing.Reset(channel);
        jitter.Reset(channel);
    }

    public void ResetJitter(ChannelId channel) => jitter.Reset(channel);

    private void PublishJitter(ChannelId channel, ReceiveJitterEventPublication? publication, DateTimeOffset now)
    {
        if (publication is not { } value) return;
        bool warning = value.Kind == ReceiveJitterEventPublicationKind.Final
            ? value.TotalMissed > 0 : value.MissedSincePrevious > 0;
        publish(new(now, warning ? DebugLogSeverity.Warning : DebugLogSeverity.Debug,
            ReceiveDiagnosticsText.FormatJitterBufferPublication(channels.State(channel).Runtime.Definition.Name, value)));
    }
}
