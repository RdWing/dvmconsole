using System.Diagnostics;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AdaptiveReceiveJitterBufferControllerTests
{
    [Theory]
    [InlineData(FneTrafficProtocol.P25, 1_620)]
    [InlineData(FneTrafficProtocol.Dmr, 540)]
    [InlineData(FneTrafficProtocol.Nxdn, 720)]
    public void AdaptiveConfigurationsUseZeroMinimumAndNinePacketCaps(
        FneTrafficProtocol protocol,
        int expectedMaximumMilliseconds)
    {
        var settings = new RxJitterBufferSetting
        {
            P25Adaptive = true,
            DmrAdaptive = true,
            NxdnAdaptive = true
        };

        ReceiveJitterBufferConfiguration configuration =
            ReceiveJitterBufferPolicy.GetConfiguration(protocol, settings);

        Assert.Equal(
            TimeSpan.Zero,
            configuration.InitialDelay);
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMaximumMilliseconds),
            configuration.MaximumDelay);
    }

    [Fact]
    public void FixedProfileKeepsTheConfiguredPacketAlignedDelay()
    {
        var controller = new AdaptiveReceiveJitterBufferController();
        var settings = new RxJitterBufferSetting
        {
            P25Milliseconds = 720,
            P25Adaptive = false
        };
        ReceiveJitterBufferConfiguration configuration =
            ReceiveJitterBufferPolicy.GetConfiguration(FneTrafficProtocol.P25, settings);

        ReceiveJitterBufferProfile profile = controller.GetProfile(
            "Alpha",
            FneTrafficProtocol.P25,
            configuration);

        Assert.Equal(TimeSpan.FromMilliseconds(720), profile.TargetDelay);
        Assert.False(profile.IsAdaptive);
    }

    [Fact]
    public void AdaptiveProfileStartsAtZeroAndUsesTheExpandedCap()
    {
        var controller = new AdaptiveReceiveJitterBufferController();
        ReceiveJitterBufferConfiguration configuration = CreateDmrConfiguration();
        long start = Stopwatch.GetTimestamp();

        controller.Observe("Alpha", CreateDmrTraffic(10, 10, start), configuration);
        controller.Observe("Alpha", CreateDmrTraffic(10, 11, Add(start, 60)), configuration);
        controller.Observe("Alpha", CreateDmrTraffic(10, 12, Add(start, 620)), configuration);

        ReceiveJitterBufferProfile profile = controller.GetProfile(
            "Alpha",
            FneTrafficProtocol.Dmr,
            configuration);
        Assert.Equal(TimeSpan.FromMilliseconds(540), profile.TargetDelay);
        Assert.True(profile.IsAdaptive);
    }

    [Fact]
    public void MissingSequenceThatArrivesOnItsExpectedClockDoesNotIncreaseDelay()
    {
        var controller = new AdaptiveReceiveJitterBufferController();
        ReceiveJitterBufferConfiguration configuration = CreateDmrConfiguration();
        long start = Stopwatch.GetTimestamp();

        controller.Observe("Alpha", CreateDmrTraffic(10, 10, start), configuration);
        controller.Observe("Alpha", CreateDmrTraffic(10, 12, Add(start, 120)), configuration);

        ReceiveJitterBufferProfile profile = controller.GetProfile(
            "Alpha",
            FneTrafficProtocol.Dmr,
            configuration);
        Assert.Equal(TimeSpan.Zero, profile.TargetDelay);
    }

    [Fact]
    public void DelayedOutOfOrderPacketIncreasesTheTargetForFutureStreams()
    {
        var controller = new AdaptiveReceiveJitterBufferController();
        ReceiveJitterBufferConfiguration configuration = CreateDmrConfiguration();
        long start = Stopwatch.GetTimestamp();

        controller.Observe("Alpha", CreateDmrTraffic(10, 10, start), configuration);
        controller.Observe("Alpha", CreateDmrTraffic(10, 12, Add(start, 120)), configuration);
        controller.Observe("Alpha", CreateDmrTraffic(10, 11, Add(start, 250)), configuration);

        Assert.Equal(
            TimeSpan.FromMilliseconds(240),
            controller.GetProfile("Alpha", FneTrafficProtocol.Dmr, configuration).TargetDelay);
    }

    [Fact]
    public void ReservedCallEndSequenceWrapDoesNotLookLikeJitter()
    {
        var controller = new AdaptiveReceiveJitterBufferController();
        ReceiveJitterBufferConfiguration configuration = CreateDmrConfiguration();
        long start = Stopwatch.GetTimestamp();

        controller.Observe("Alpha", CreateDmrTraffic(10, ushort.MaxValue - 1, start), configuration);
        controller.Observe("Alpha", CreateDmrTraffic(10, 0, Add(start, 60)), configuration);
        controller.Observe("Alpha", CreateDmrTraffic(10, 1, Add(start, 120)), configuration);

        Assert.Equal(
            TimeSpan.Zero,
            controller.GetProfile("Alpha", FneTrafficProtocol.Dmr, configuration).TargetDelay);
    }

    [Fact]
    public void EstimatesAreIsolatedByConnectionAndProtocol()
    {
        var controller = new AdaptiveReceiveJitterBufferController();
        ReceiveJitterBufferConfiguration dmr = CreateDmrConfiguration();
        var p25Settings = new RxJitterBufferSetting { P25Adaptive = true };
        ReceiveJitterBufferConfiguration p25 =
            ReceiveJitterBufferPolicy.GetConfiguration(FneTrafficProtocol.P25, p25Settings);
        long start = Stopwatch.GetTimestamp();

        controller.Observe("Alpha", CreateDmrTraffic(10, 10, start), dmr);
        controller.Observe("Alpha", CreateDmrTraffic(10, 11, Add(start, 190)), dmr);

        Assert.Equal(
            TimeSpan.FromMilliseconds(180),
            controller.GetProfile("Alpha", FneTrafficProtocol.Dmr, dmr).TargetDelay);
        Assert.Equal(
            TimeSpan.Zero,
            controller.GetProfile("Beta", FneTrafficProtocol.Dmr, dmr).TargetDelay);
        Assert.Equal(
            TimeSpan.Zero,
            controller.GetProfile("Alpha", FneTrafficProtocol.P25, p25).TargetDelay);
    }

    [Fact]
    public void DecreasesOnePacketOnlyAfterThreeCleanCompletedCalls()
    {
        var controller = new AdaptiveReceiveJitterBufferController();
        ReceiveJitterBufferConfiguration configuration = CreateDmrConfiguration();
        long start = Stopwatch.GetTimestamp();

        ObserveCall(controller, configuration, streamId: 1, start, secondPacketDelayMilliseconds: 190);
        Assert.Equal(
            TimeSpan.FromMilliseconds(180),
            controller.GetProfile("Alpha", FneTrafficProtocol.Dmr, configuration).TargetDelay);

        for (uint streamId = 2; streamId <= 3; streamId++)
        {
            start = Add(start, 1_000);
            ObserveCall(controller, configuration, streamId, start, secondPacketDelayMilliseconds: 60);
        }
        Assert.Equal(
            TimeSpan.FromMilliseconds(180),
            controller.GetProfile("Alpha", FneTrafficProtocol.Dmr, configuration).TargetDelay);

        start = Add(start, 1_000);
        ObserveCall(controller, configuration, streamId: 4, start, secondPacketDelayMilliseconds: 60);
        Assert.Equal(
            TimeSpan.FromMilliseconds(120),
            controller.GetProfile("Alpha", FneTrafficProtocol.Dmr, configuration).TargetDelay);

        for (uint streamId = 5; streamId <= 7; streamId++)
        {
            start = Add(start, 1_000);
            ObserveCall(controller, configuration, streamId, start, secondPacketDelayMilliseconds: 60);
        }
        Assert.Equal(
            TimeSpan.FromMilliseconds(60),
            controller.GetProfile("Alpha", FneTrafficProtocol.Dmr, configuration).TargetDelay);

        for (uint streamId = 8; streamId <= 10; streamId++)
        {
            start = Add(start, 1_000);
            ObserveCall(controller, configuration, streamId, start, secondPacketDelayMilliseconds: 60);
        }
        Assert.Equal(
            TimeSpan.Zero,
            controller.GetProfile("Alpha", FneTrafficProtocol.Dmr, configuration).TargetDelay);
    }

    private static void ObserveCall(
        AdaptiveReceiveJitterBufferController controller,
        ReceiveJitterBufferConfiguration configuration,
        uint streamId,
        long start,
        int secondPacketDelayMilliseconds)
    {
        controller.Observe("Alpha", CreateDmrTraffic(streamId, 10, start), configuration);
        controller.Observe(
            "Alpha",
            CreateDmrTraffic(streamId, 11, Add(start, secondPacketDelayMilliseconds)),
            configuration);
        controller.Observe(
            "Alpha",
            CreateDmrTraffic(streamId, ushort.MaxValue, Add(start, secondPacketDelayMilliseconds + 1), terminator: true),
            configuration);
    }

    private static ReceiveJitterBufferConfiguration CreateDmrConfiguration()
        => ReceiveJitterBufferPolicy.GetConfiguration(
            FneTrafficProtocol.Dmr,
            new RxJitterBufferSetting { DmrAdaptive = true });

    private static FneTrafficFrame CreateDmrTraffic(
        uint streamId,
        ushort sequence,
        long transportTimestamp,
        bool terminator = false)
        => new(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 2,
            destinationId: 100,
            slot: 1,
            callType: "GROUP",
            frameType: terminator ? "TERMINATOR" : "VOICE",
            subtype: terminator ? "TERMINATOR_WITH_LC" : "VOICE",
            packetSequence: sequence,
            streamId,
            payload: [],
            fneBoundaryTimestamp: transportTimestamp + 1,
            transportIngressTimestamp: transportTimestamp);

    private static long Add(long timestamp, int milliseconds)
        => timestamp + (long)Math.Round(
            TimeSpan.FromMilliseconds(milliseconds).TotalSeconds * Stopwatch.Frequency);
}
