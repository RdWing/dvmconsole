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
            PhysicalOutputStarvation: TimeSpan.FromMilliseconds(40),
            PendingPhysicalOutputStarvation: TimeSpan.FromMilliseconds(20),
            PhysicalOutputCallbackCount: 123,
            PhysicalOutputCallbackAge: TimeSpan.FromMilliseconds(5),
            AgedLiveSamples: 160,
            LaneDiagnostics:
            [
                new AudioMixerLaneDiagnostics(
                    "East Bay/Dispatch",
                    DroppedSamples: 160,
                    OverflowResynchronizations: 1,
                    GapFilledSamples: 240,
                    AgedLiveSamples: 160,
                    PeakBufferedFrames: 18)
            ]);
        var pipeline = new ReceiveWorkQueueDiagnostics(
            ProcessedFrames: 10,
            MaximumInterArrivalDelay: TimeSpan.FromMilliseconds(500),
            MaximumIngressToQueueDelay: TimeSpan.FromMilliseconds(2),
            MaximumQueueDelay: TimeSpan.FromMilliseconds(3),
            MaximumProcessingDuration: TimeSpan.FromMilliseconds(4),
            MaximumEndToEndDelay: TimeSpan.FromMilliseconds(5),
            MaximumTransportInterArrivalDelay: TimeSpan.FromMilliseconds(480),
            MaximumTransportToFneBoundaryDelay: TimeSpan.FromMilliseconds(8),
            JitterBufferReorderedPackets: 2,
            JitterBufferDeadlineMissedPackets: 1);

        string message = ReceiveDiagnosticsText.FormatWarning(
            "Dispatch",
            streamId: 42,
            warning,
            receiveSelected: true,
            playback,
            pipeline);

        Assert.Contains("(RX selected)", message);
        Assert.Contains("stream 42", message);
        Assert.Contains("shared output mixer dropped 20 ms", message);
        Assert.Contains("stale live audio aged 20 ms", message);
        Assert.Contains("physical starvation 40 ms", message);
        Assert.Contains("pending physical starvation 20 ms", message);
        Assert.Contains("output callbacks 123 (age 5 ms)", message);
        Assert.Contains("live gap fill 30 ms", message);
        Assert.Contains("output-policy discarded 60 ms", message);
        Assert.Contains("last overflow East Bay/Dispatch (20 ms cumulative)", message);
        Assert.Contains("stream pipeline maximum UDP inter-arrival 480 ms", message);
        Assert.Contains("socket-to-FNE 8 ms", message);
        Assert.Contains("FNE inter-arrival 500 ms", message);
        Assert.Contains("jitter reordered this stream 2", message);
        Assert.Contains("jitter deadline misses this stream 1", message);
        Assert.Contains("worst lane East Bay/Dispatch", message);
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
            EndToEndDelay: TimeSpan.FromMilliseconds(6),
            TransportInterArrivalDelay: TimeSpan.FromMilliseconds(440),
            TransportToFneBoundaryDelay: TimeSpan.FromMilliseconds(1),
            JitterBufferTargetDelay: TimeSpan.FromMilliseconds(180));
        var maximums = new ReceiveWorkQueueDiagnostics(
            ProcessedFrames: 5,
            MaximumInterArrivalDelay: TimeSpan.FromMilliseconds(500),
            MaximumIngressToQueueDelay: TimeSpan.FromMilliseconds(2),
            MaximumQueueDelay: TimeSpan.FromMilliseconds(3),
            MaximumProcessingDuration: TimeSpan.FromMilliseconds(4),
            MaximumEndToEndDelay: TimeSpan.FromMilliseconds(20),
            MaximumJitterBufferTargetDelay: TimeSpan.FromMilliseconds(180));

        string message = ReceiveDiagnosticsText.FormatPipelineDelay(
            "Dispatch",
            latest,
            maximums);

        Assert.Contains("FNE inter-arrival 450 ms", message);
        Assert.Contains("UDP inter-arrival 440 ms", message);
        Assert.Contains("socket-to-FNE 1 ms", message);
        Assert.Contains("FNE boundary-to-queue 1 ms", message);
        Assert.Contains("fixed jitter 180 ms", message);
        Assert.Contains("total FNE-to-mixer 6 ms", message);
        Assert.Contains("stream maximum total FNE-to-mixer 20 ms", message);
        Assert.EndsWith("stream 34, sequence 12.", message);

        string adaptiveMessage = ReceiveDiagnosticsText.FormatPipelineDelay(
            "Dispatch",
            latest with { AdaptiveJitterBuffer = true },
            maximums);
        Assert.Contains("adaptive jitter target 180 ms", adaptiveMessage);
    }

    [Fact]
    public void DescribesSuccessfulJitterBufferReordering()
    {
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            1,
            2,
            100,
            1,
            "GROUP",
            "VOICE",
            "VOICE",
            11,
            34,
            []);
        var timing = new ReceiveWorkItemTiming(
            traffic,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(120),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(120),
            JitterBufferReorderedPacket: true);

        string message = ReceiveDiagnosticsText.FormatJitterBufferEvent("Dispatch", timing);

        Assert.Contains("restored delayed sequence 11", message);
        Assert.Contains("before playout", message);
    }
}
