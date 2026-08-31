using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveJitterBufferTelemetryTests
{
    [Fact]
    public void FormatsLearnedAdaptiveTargetsAndConnectionEffectiveness()
    {
        var telemetry = new ReceiveJitterBufferTelemetry(
            P25LearnedDelay: TimeSpan.Zero,
            DmrLearnedDelay: TimeSpan.FromMilliseconds(180),
            NxdnLearnedDelay: TimeSpan.FromMilliseconds(240),
            P25Adaptive: true,
            DmrAdaptive: false,
            NxdnAdaptive: true,
            RestoredDelayedPackets: 3,
            DeadlineMissedPackets: 1);

        Assert.Equal("Adaptive learned · P25 0 ms · NXDN 240 ms", telemetry.LearnedText);
        Assert.Equal(
            "Jitter effectiveness · restored 3 delayed packets before playout · deadline misses 1",
            telemetry.EffectivenessText);
    }
}
