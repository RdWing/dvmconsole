using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class OperationalHealthPresentationTests
{
    [Fact]
    public void FixedBucketsReportDeterministicPercentileUpperBounds()
    {
        var tracker = new FixedBucketLatencyTracker();
        Observe(tracker, TimeSpan.FromMilliseconds(2), 50);
        Observe(tracker, TimeSpan.FromMilliseconds(16), 45);
        Observe(tracker, TimeSpan.FromMilliseconds(64), 4);
        Observe(tracker, TimeSpan.FromMilliseconds(256), 1);

        LatencyPercentiles percentiles = tracker.Snapshot();

        Assert.Equal(TimeSpan.FromMilliseconds(2), percentiles.P50);
        Assert.Equal(TimeSpan.FromMilliseconds(16), percentiles.P95);
        Assert.Equal(TimeSpan.FromMilliseconds(64), percentiles.P99);
    }

    [Fact]
    public void OverflowBucketReportsObservedLatencyInsteadOfCappingAtFiveSeconds()
    {
        var tracker = new FixedBucketLatencyTracker();
        tracker.Observe(TimeSpan.FromSeconds(9));

        LatencyPercentiles percentiles = tracker.Snapshot();

        Assert.Equal(TimeSpan.FromSeconds(9), percentiles.P50);
        Assert.Equal(TimeSpan.FromSeconds(9), percentiles.P95);
        Assert.Equal(TimeSpan.FromSeconds(9), percentiles.P99);
    }

    [Fact]
    public void MicrophonePresentationUsesExplicitNonColorSafetyStates()
    {
        var ready = new MicrophoneHealth(
            MicrophoneHealthState.Ready,
            2,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            null);
        var stale = ready with
        {
            State = MicrophoneHealthState.Stale,
            LastSampleAge = TimeSpan.FromMilliseconds(500)
        };
        var faulted = ready with
        {
            State = MicrophoneHealthState.Faulted,
            Fault = "capture stopped"
        };

        Assert.StartsWith("Mic: READY", OperationalHealthPresentation.FormatMicrophone(ready, blocked: false));
        Assert.StartsWith("Mic: BLOCKED", OperationalHealthPresentation.FormatMicrophone(ready, blocked: true));
        Assert.StartsWith("Mic: STALE", OperationalHealthPresentation.FormatMicrophone(stale, blocked: false));
        Assert.Equal(
            "Mic: FAULTED · capture stopped",
            OperationalHealthPresentation.FormatMicrophone(faulted, blocked: false));
        Assert.Contains(
            "generation 2 · cadence 20 ms",
            OperationalHealthPresentation.FormatMicrophoneEngineering(ready),
            StringComparison.Ordinal);
    }

    private static void Observe(
        FixedBucketLatencyTracker tracker,
        TimeSpan latency,
        int count)
    {
        for (int index = 0; index < count; index++)
            tracker.Observe(latency);
    }
}
