using DvmConsole.FneClient;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveDiagnosticsTextTests
{
    [Fact]
    public void FormatsPacketPlaybackAndPipelineEvidenceByResponsibility()
    {
        var warning = new ReceiveWarningDiagnostics(1, 2, 3, 4, 5);
        var playback = new AudioMixerDiagnostics(
            DroppedSamples: 160,
            OverflowResynchronizations: 1,
            ProtectedFrames: 0,
            LowBufferRecoveries: 2,
            LatePumpWakes: 3,
            MaximumPumpLateness: TimeSpan.FromMilliseconds(25),
            PeakBufferedFrames: 18,
            StartupBufferedFrames: 18,
            MaximumBufferedFrames: 50,
            TargetOutputBufferedFrames: 8,
            LastDroppedLane: "East Bay/Dispatch",
            LastDroppedLaneSamples: 160,
            GapFilledSamples: 240,
            SuppressedLiveConcealmentSamples: 320,
            TransitionDiscardedSamples: 480,
            PhysicalOutputStarvation: TimeSpan.FromMilliseconds(40));
        var pipeline = new ReceiveWorkQueueDiagnostics(
            ProcessedFrames: 10,
            MaximumInterArrivalDelay: TimeSpan.FromMilliseconds(500),
            MaximumIngressToQueueDelay: TimeSpan.FromMilliseconds(2),
            MaximumQueueDelay: TimeSpan.FromMilliseconds(3),
            MaximumProcessingDuration: TimeSpan.FromMilliseconds(4),
            MaximumEndToEndDelay: TimeSpan.FromMilliseconds(5));

        string message = ReceiveDiagnosticsText.FormatWarning(
            "Dispatch",
            warning,
            receiveSelected: true,
            playback,
            pipeline);

        Assert.Contains("(RX selected)", message);
        Assert.Contains("shared output mixer dropped 20 ms", message);
        Assert.Contains("physical starvation 40 ms", message);
        Assert.Contains("live gap fill 30 ms", message);
        Assert.Contains("cold-transition discarded 60 ms", message);
        Assert.Contains("last overflow East Bay/Dispatch (20 ms cumulative)", message);
        Assert.Contains("maximum FNE inter-arrival 500 ms", message);
    }

    [Fact]
    public void FormatsLatestPipelineTimingSeparatelyFromHighWaterMark()
    {
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId: 2,
            destinationId: 100,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "LDU1",
            packetSequence: 12,
            streamId: 34,
            payload: []);
        var latest = new ReceiveWorkItemTiming(
            traffic,
            InterArrivalDelay: TimeSpan.FromMilliseconds(450),
            IngressToQueueDelay: TimeSpan.FromMilliseconds(1),
            QueueDelay: TimeSpan.FromMilliseconds(2),
            ProcessingDuration: TimeSpan.FromMilliseconds(3),
            EndToEndDelay: TimeSpan.FromMilliseconds(6));
        var maximums = new ReceiveWorkQueueDiagnostics(
            ProcessedFrames: 5,
            MaximumInterArrivalDelay: TimeSpan.FromMilliseconds(500),
            MaximumIngressToQueueDelay: TimeSpan.FromMilliseconds(2),
            MaximumQueueDelay: TimeSpan.FromMilliseconds(3),
            MaximumProcessingDuration: TimeSpan.FromMilliseconds(4),
            MaximumEndToEndDelay: TimeSpan.FromMilliseconds(20));

        string message = ReceiveDiagnosticsText.FormatPipelineDelay(
            "Dispatch",
            latest,
            maximums);

        Assert.Contains("FNE inter-arrival 450 ms", message);
        Assert.Contains("FNE boundary-to-queue 1 ms", message);
        Assert.Contains("FNE-to-mixer 6 ms", message);
        Assert.Contains("maximum FNE-to-mixer 20 ms", message);
        Assert.EndsWith("stream 34, sequence 12.", message);
    }
}
