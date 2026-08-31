using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

// Formats receive diagnostics independently from UI dispatch and collection.
// The reporter classes decide when to publish; this type decides only how an
// operator-facing diagnostic is described.
internal static class ReceiveDiagnosticsText
{
    private const double MillisecondsPerSecond = 1000.0;
    private const int MixerFrameDurationMilliseconds = 20;

    public static string FormatWarning(
        string channelName,
        uint streamId,
        ReceiveWarningDiagnostics warning,
        bool receiveSelected,
        AudioMixerDiagnostics? playback,
        ReceiveWorkQueueDiagnostics pipeline,
        EpisodeLivePlayoutDiagnostics arbitration = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        string selectionState = receiveSelected ? "RX selected" : "RX not selected";
        return $"RX {channelName}, stream {streamId}: {warning.SummaryText} ({selectionState})" +
               FormatPlayback(playback) +
               FormatArbitration(arbitration) +
               FormatPipelineMaximums(pipeline);
    }

    public static string FormatPipelineDelay(
        string channelName,
        ReceiveWorkItemTiming latest,
        ReceiveWorkQueueDiagnostics maximums)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        string jitterTarget = latest.AdaptiveJitterBuffer
            ? $"adaptive jitter target {latest.JitterBufferTargetDelay.TotalMilliseconds:0} ms"
            : $"fixed jitter {latest.JitterBufferTargetDelay.TotalMilliseconds:0} ms";
        string queueStages = latest.HasQueueDelayBreakdown
            ? $"jitter/worker queue {latest.QueueDelay.TotalMilliseconds:0} ms " +
              $"(jitter hold {latest.JitterBufferHoldDuration.TotalMilliseconds:0} ms; " +
              $"{jitterTarget}; worker backlog {latest.WorkerBacklogDuration.TotalMilliseconds:0} ms)"
            : $"jitter/decoder queue {latest.QueueDelay.TotalMilliseconds:0} ms ({jitterTarget})";
        string processingPrivacy = latest.EncryptedSessionProcessing switch
        {
            true => "encrypted key/decrypt/decode/mixer",
            false => "clear decode/mixer",
            null => "decode/mixer (privacy not yet signaled)"
        };
        string processingStages = latest.HasSessionProcessingBreakdown
            ? $"session gate {latest.SessionGateDelay.TotalMilliseconds:0} ms, " +
              $"{processingPrivacy} {latest.SessionProcessingDuration.TotalMilliseconds:0} ms, " +
              $"processing total {latest.ProcessingDuration.TotalMilliseconds:0} ms"
            : $"decode/mixer {latest.ProcessingDuration.TotalMilliseconds:0} ms";
        return $"RX pipeline delay on {channelName}: " +
               $"UDP inter-arrival {latest.TransportInterArrivalDelay.TotalMilliseconds:0} ms, " +
               $"socket-to-FNE {latest.TransportToApplicationBoundaryDelay.TotalMilliseconds:0} ms, " +
               $"FNE inter-arrival {latest.InterArrivalDelay.TotalMilliseconds:0} ms, " +
               $"FNE boundary-to-queue {latest.IngressToQueueDelay.TotalMilliseconds:0} ms, " +
               queueStages + ", " +
               processingStages + ", " +
               $"total FNE-to-mixer {latest.EndToEndDelay.TotalMilliseconds:0} ms; " +
               $"stream maximum total FNE-to-mixer {maximums.MaximumEndToEndDelay.TotalMilliseconds:0} ms; " +
               $"stream {latest.Traffic.StreamId}, sequence {latest.Traffic.PacketSequence}.";
    }

    public static string FormatJitterBufferEvent(
        string channelName,
        ReceiveWorkItemTiming timing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        if (timing.JitterBufferReorderedPacket)
        {
            string missedSuffix = timing.JitterBufferDeadlineMissedPackets > 0
                ? $" The deadline also advanced across {timing.JitterBufferDeadlineMissedPackets} " +
                  $"missing network packet{(timing.JitterBufferDeadlineMissedPackets == 1 ? string.Empty : "s")}."
                : string.Empty;
            return $"RX jitter buffer on {channelName}: restored delayed sequence " +
                   $"{timing.Traffic.PacketSequence} to playout order for stream " +
                   $"{timing.Traffic.StreamId} before playout.{missedSuffix}";
        }

        int missed = timing.JitterBufferDeadlineMissedPackets;
        return $"RX jitter buffer deadline on {channelName}: advanced across " +
               $"{missed} missing network packet{(missed == 1 ? string.Empty : "s")} " +
               $"before sequence {timing.Traffic.PacketSequence} on stream " +
               $"{timing.Traffic.StreamId}.";
    }

    public static string FormatJitterBufferPublication(
        string channelName,
        ReceiveJitterEventPublication publication)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        string counts = $"restored {publication.ReorderedSincePrevious:N0} delayed packet" +
            $"{(publication.ReorderedSincePrevious == 1 ? string.Empty : "s")} and advanced across " +
            $"{publication.MissedSincePrevious:N0} missing network packet" +
            $"{(publication.MissedSincePrevious == 1 ? string.Empty : "s")}";
        string totals = $"cumulative restored {publication.TotalReordered:N0}, " +
            $"missing {publication.TotalMissed:N0}";
        return publication.Kind switch
        {
            ReceiveJitterEventPublicationKind.First =>
                $"RX jitter buffer first event on {channelName}, physical stream " +
                $"{publication.StreamId}: {counts}; sequence {publication.LatestSequence}.",
            ReceiveJitterEventPublicationKind.Periodic =>
                $"RX jitter buffer update on {channelName}, physical stream " +
                $"{publication.StreamId}: since previous report {counts}; {totals}; " +
                $"latest sequence {publication.LatestSequence}.",
            _ =>
                $"RX jitter buffer final summary on {channelName}, physical stream " +
                $"{publication.StreamId}: {totals}; since previous report {counts}."
        };
    }

    private static string FormatPlayback(AudioMixerDiagnostics? playback)
    {
        if (playback is null)
            return string.Empty;

        string physicalStarvation = playback.PhysicalOutputStarvation is TimeSpan starvation
            ? $"physical starvation {starvation.TotalMilliseconds:0} ms, "
            : string.Empty;
        string pendingPhysicalStarvation =
            playback.PendingPhysicalOutputStarvation is TimeSpan pendingStarvation &&
            pendingStarvation > TimeSpan.Zero
                ? $"pending physical starvation {pendingStarvation.TotalMilliseconds:0} ms, "
                : string.Empty;
        string callbackHealth = playback.PhysicalOutputCallbackCount is long callbackCount
            ? $"output callbacks {callbackCount}" +
              (playback.PhysicalOutputCallbackAge is TimeSpan callbackAge
                  ? $" (age {callbackAge.TotalMilliseconds:0} ms), "
                  : ", ")
            : string.Empty;
        string pumpActivity = playback.OutputPump.TotalWakeups == 0
            ? string.Empty
            : $", pump signals {playback.OutputPump.SignalRequests:N0} " +
              $"(coalesced {playback.OutputPump.CoalescedSignalRequests:N0}), " +
              $"wakeups signaled {playback.OutputPump.SignaledWakeups:N0} / " +
              $"timer {playback.OutputPump.TimeoutWakeups:N0} / " +
              $"empty {playback.OutputPump.NoWorkWakeups:N0}, " +
              $"idle waits {playback.OutputPump.IdleWaits:N0}, " +
              $"frames written {playback.OutputPump.FramesWritten:N0} " +
              $"(multi-frame drains {playback.OutputPump.MultiFrameWakeups:N0})";
        string latestOverflow = playback.LastDroppedLane is null
            ? string.Empty
            : $", last overflow {playback.LastDroppedLane} " +
              $"({SamplesToMilliseconds(playback.LastDroppedLaneSamples):0} ms cumulative)";
        AudioMixerLaneDiagnostics? worstLane = playback.LaneDiagnostics?
            .OrderByDescending(lane => lane.DroppedSamples)
            .ThenByDescending(lane => lane.GapFilledSamples)
            .FirstOrDefault(lane => lane.DroppedSamples > 0 || lane.GapFilledSamples > 0);
        string worstLaneText = worstLane is null
            ? string.Empty
            : $", worst lane {worstLane.Label} " +
              $"(dropped {SamplesToMilliseconds(worstLane.DroppedSamples):0} ms, " +
              $"gap fill {SamplesToMilliseconds(worstLane.GapFilledSamples):0} ms)";

        return $"; shared output mixer dropped {SamplesToMilliseconds(playback.DroppedSamples):0} ms, " +
               $"stale live audio aged {SamplesToMilliseconds(playback.AgedLiveSamples):0} ms, " +
               $"overflow resyncs {playback.OverflowResynchronizations}, " +
               physicalStarvation +
               pendingPhysicalStarvation +
               callbackHealth +
               $"live gap fill " +
               $"{SamplesToMilliseconds(playback.GapFilledSamples):0} ms, " +
               $"late concealment skipped for live audio " +
               $"{SamplesToMilliseconds(playback.SuppressedLiveConcealmentSamples):0} ms, " +
               $"output-policy discarded " +
               $"{SamplesToMilliseconds(playback.TransitionDiscardedSamples):0} ms, " +
               $"low-buffer recoveries {playback.LowBufferRecoveries}, " +
               $"peak queued {FramesToMilliseconds(playback.PeakBufferedFrames)} ms, " +
               $"playout cushion {FramesToMilliseconds(playback.StartupBufferedFrames)} ms, " +
               $"lane cap {FramesToMilliseconds(playback.MaximumBufferedFrames)} ms, " +
               $"output target {FramesToMilliseconds(playback.TargetOutputBufferedFrames)} ms, " +
               $"late pump wakes {playback.LatePumpWakes} " +
               $"(max {playback.MaximumPumpLateness.TotalMilliseconds:0} ms)" +
               pumpActivity +
               latestOverflow +
               worstLaneText;
    }

    private static string FormatPipelineMaximums(ReceiveWorkQueueDiagnostics pipeline)
        => pipeline.ProcessedFrames == 0
            ? string.Empty
            : $"; RX stream pipeline maximum UDP inter-arrival " +
              $"{pipeline.MaximumTransportInterArrivalDelay.TotalMilliseconds:0} ms, " +
              $"socket-to-FNE {pipeline.MaximumTransportToApplicationBoundaryDelay.TotalMilliseconds:0} ms, " +
              $"FNE inter-arrival " +
              $"{pipeline.MaximumInterArrivalDelay.TotalMilliseconds:0} ms, " +
              $"FNE boundary-to-queue {pipeline.MaximumIngressToQueueDelay.TotalMilliseconds:0} ms, " +
              $"jitter/worker queue {pipeline.MaximumQueueDelay.TotalMilliseconds:0} ms " +
              $"(jitter hold max {pipeline.MaximumJitterBufferHoldDuration.TotalMilliseconds:0} ms, " +
              $"jitter target up to {pipeline.MaximumJitterBufferTargetDelay.TotalMilliseconds:0} ms, " +
              $"worker backlog max {pipeline.MaximumWorkerBacklogDuration.TotalMilliseconds:0} ms), " +
              $"session gate max {pipeline.MaximumSessionGateDelay.TotalMilliseconds:0} ms, " +
              $"session processing max {pipeline.MaximumSessionProcessingDuration.TotalMilliseconds:0} ms, " +
              $"processing total max {pipeline.MaximumProcessingDuration.TotalMilliseconds:0} ms, " +
              $"total FNE-to-mixer {pipeline.MaximumEndToEndDelay.TotalMilliseconds:0} ms, " +
              $"jitter reordered this stream {pipeline.JitterBufferReorderedPackets:N0}, " +
              $"jitter deadline misses this stream {pipeline.JitterBufferDeadlineMissedPackets:N0}";

    private static string FormatArbitration(EpisodeLivePlayoutDiagnostics diagnostics)
        => diagnostics.ProducerHandoffs == 0 && diagnostics.SuppressedRetiredSamples == 0
            ? string.Empty
            : $"; live episode handoffs {diagnostics.ProducerHandoffs:N0}, " +
              $"retired-stream audio kept from live output " +
              $"{SamplesToMilliseconds(diagnostics.SuppressedRetiredSamples):0} ms";

    private static double SamplesToMilliseconds(long samples)
        => samples * MillisecondsPerSecond /
           PcmAudioFormat.Voice8KhzMono16Bit.SampleRate;

    private static int FramesToMilliseconds(int frames)
        => checked(frames * MixerFrameDurationMilliseconds);
}
