using DvmConsole.Audio;
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
        ReceiveWarningDiagnostics warning,
        bool receiveSelected,
        AudioMixerDiagnostics? playback,
        ReceiveWorkQueueDiagnostics pipeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        string selectionState = receiveSelected ? "RX selected" : "RX not selected";
        return $"RX {channelName}: {warning.SummaryText} ({selectionState})" +
               FormatPlayback(playback) +
               FormatPipelineMaximums(pipeline);
    }

    public static string FormatPipelineDelay(
        string channelName,
        ReceiveWorkItemTiming latest,
        ReceiveWorkQueueDiagnostics maximums)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        return $"RX pipeline delay on {channelName}: " +
               $"FNE inter-arrival {latest.InterArrivalDelay.TotalMilliseconds:0} ms, " +
               $"FNE boundary-to-queue {latest.IngressToQueueDelay.TotalMilliseconds:0} ms, " +
               $"decoder queue {latest.QueueDelay.TotalMilliseconds:0} ms, " +
               $"decode/mixer {latest.ProcessingDuration.TotalMilliseconds:0} ms, " +
               $"FNE-to-mixer {latest.EndToEndDelay.TotalMilliseconds:0} ms; " +
               $"maximum FNE-to-mixer {maximums.MaximumEndToEndDelay.TotalMilliseconds:0} ms; " +
               $"stream {latest.Traffic.StreamId}, sequence {latest.Traffic.PacketSequence}.";
    }

    private static string FormatPlayback(AudioMixerDiagnostics? playback)
    {
        if (playback is null)
            return string.Empty;

        string physicalStarvation = playback.PhysicalOutputStarvation is TimeSpan starvation
            ? $"physical starvation {starvation.TotalMilliseconds:0} ms, "
            : string.Empty;
        string latestOverflow = playback.LastDroppedLane is null
            ? string.Empty
            : $", last overflow {playback.LastDroppedLane} " +
              $"({SamplesToMilliseconds(playback.LastDroppedLaneSamples):0} ms cumulative)";

        return $"; shared output mixer dropped {SamplesToMilliseconds(playback.DroppedSamples):0} ms, " +
               $"overflow resyncs {playback.OverflowResynchronizations}, " +
               physicalStarvation +
               $"live gap fill " +
               $"{SamplesToMilliseconds(playback.GapFilledSamples):0} ms, " +
               $"late concealment skipped for live audio " +
               $"{SamplesToMilliseconds(playback.SuppressedLiveConcealmentSamples):0} ms, " +
               $"cold-transition discarded " +
               $"{SamplesToMilliseconds(playback.TransitionDiscardedSamples):0} ms, " +
               $"low-buffer recoveries {playback.LowBufferRecoveries}, " +
               $"peak queued {FramesToMilliseconds(playback.PeakBufferedFrames)} ms, " +
               $"playout cushion {FramesToMilliseconds(playback.StartupBufferedFrames)} ms, " +
               $"lane cap {FramesToMilliseconds(playback.MaximumBufferedFrames)} ms, " +
               $"output target {FramesToMilliseconds(playback.TargetOutputBufferedFrames)} ms, " +
               $"late pump wakes {playback.LatePumpWakes} " +
               $"(max {playback.MaximumPumpLateness.TotalMilliseconds:0} ms)" +
               latestOverflow;
    }

    private static string FormatPipelineMaximums(ReceiveWorkQueueDiagnostics pipeline)
        => pipeline.ProcessedFrames == 0
            ? string.Empty
            : $"; RX pipeline maximum FNE inter-arrival " +
              $"{pipeline.MaximumInterArrivalDelay.TotalMilliseconds:0} ms, " +
              $"FNE boundary-to-queue {pipeline.MaximumIngressToQueueDelay.TotalMilliseconds:0} ms, " +
              $"decoder queue {pipeline.MaximumQueueDelay.TotalMilliseconds:0} ms, " +
              $"decode/mixer {pipeline.MaximumProcessingDuration.TotalMilliseconds:0} ms, " +
              $"FNE-to-mixer {pipeline.MaximumEndToEndDelay.TotalMilliseconds:0} ms";

    private static double SamplesToMilliseconds(long samples)
        => samples * MillisecondsPerSecond /
           PcmAudioFormat.Voice8KhzMono16Bit.SampleRate;

    private static int FramesToMilliseconds(int frames)
        => checked(frames * MixerFrameDurationMilliseconds);
}
