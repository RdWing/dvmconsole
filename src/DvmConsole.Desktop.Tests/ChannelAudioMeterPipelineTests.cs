using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ChannelAudioMeterPipelineTests
{
    [Fact]
    public void SignalsOnlyTheTransitionFromIdleAndReturnsToIdleAfterDecay()
    {
        ChannelViewModel channel = CreateReceivingChannel(FneTrafficProtocol.P25, streamId: 7);
        var pipeline = new ChannelAudioMeterPipeline();
        short[] frame = Enumerable.Repeat((short)8_000, 160).ToArray();

        Assert.True(pipeline.Observe(channel, 7, frame, ChannelAudioDirection.Receive));
        Assert.False(pipeline.Observe(channel, 7, frame, ChannelAudioDirection.Receive));
        Assert.True(pipeline.HasActivity);

        for (int index = 0; index < 20 && pipeline.HasActivity; index++)
            pipeline.Advance();

        Assert.False(pipeline.HasActivity);
        Assert.True(pipeline.Observe(channel, 7, frame, ChannelAudioDirection.Receive));
    }

    [Fact]
    public void P25FrameBurstIsPresentedAcrossProtocolNeutralRefreshes()
    {
        ChannelViewModel channel = CreateReceivingChannel(FneTrafficProtocol.P25, streamId: 7);
        var pipeline = new ChannelAudioMeterPipeline();
        short[] frame = Enumerable.Repeat((short)8_000, 160).ToArray();

        for (int index = 0; index < 9; index++)
            pipeline.Observe(channel, streamId: 7, frame, ChannelAudioDirection.Receive);

        ChannelAudioMeterUpdate[] updates = Enumerable.Range(0, 4)
            .Select(_ => Assert.Single(pipeline.Advance()))
            .ToArray();

        Assert.All(updates, update =>
        {
            Assert.Equal((uint)7, update.StreamId);
            Assert.True(update.Level > 0);
        });
    }

    [Fact]
    public void NewStreamDoesNotDiscardAnIndependentMeterWindow()
    {
        ChannelViewModel channel = CreateReceivingChannel(FneTrafficProtocol.P25, streamId: 8);
        var pipeline = new ChannelAudioMeterPipeline();

        pipeline.Observe(
            channel,
            streamId: 7,
            Enumerable.Repeat((short)30_000, 160).ToArray(),
            ChannelAudioDirection.Receive);
        pipeline.Observe(
            channel,
            streamId: 8,
            Enumerable.Repeat((short)1_000, 160).ToArray(),
            ChannelAudioDirection.Receive);

        ChannelAudioMeterUpdate[] updates = pipeline.Advance().ToArray();
        ChannelAudioMeterUpdate update = Assert.Single(
            updates,
            candidate => candidate.StreamId == 8);

        Assert.Equal(2, updates.Length);
        Assert.Equal((uint)8, update.StreamId);
        Assert.InRange(update.Level, 0.1, 20);
    }

    [Fact]
    public void CollidingStreamsKeepIndependentMeterWindows()
    {
        ChannelViewModel channel = CreateReceivingChannel(FneTrafficProtocol.P25, streamId: 7);
        var pipeline = new ChannelAudioMeterPipeline();

        pipeline.Observe(
            channel,
            streamId: 7,
            Enumerable.Repeat((short)12_000, 160).ToArray(),
            ChannelAudioDirection.Receive);
        pipeline.Observe(
            channel,
            streamId: 8,
            Enumerable.Repeat((short)2_000, 160).ToArray(),
            ChannelAudioDirection.Receive);

        ChannelAudioMeterUpdate[] updates = pipeline.Advance().ToArray();

        Assert.Equal(2, updates.Length);
        Assert.Contains(updates, update => update.StreamId == 7 && update.Level > 0);
        Assert.Contains(updates, update => update.StreamId == 8 && update.Level > 0);
    }

    [Fact]
    public void FastReceiveStreamCanPresentBeforeUiLifecycleCatchesUp()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        channel.SetAudioEnabled(true);
        channel.MarkReceiveAudioMeterActive(7);
        var pipeline = new ChannelAudioMeterPipeline();
        pipeline.Observe(
            channel,
            streamId: 7,
            Enumerable.Repeat((short)12_000, 160).ToArray(),
            ChannelAudioDirection.Receive);

        ChannelAudioMeterUpdate update = Assert.Single(pipeline.Advance());
        channel.SetAudioLevel(update.Level, update.Direction, update.StreamId);

        Assert.True(channel.AudioLevel > 0);
    }

    [Fact]
    public void AudioLevelNotificationDoesNotInvalidateTheVolumeBinding()
    {
        ChannelViewModel channel = CreateReceivingChannel(FneTrafficProtocol.Dmr, streamId: 9);
        var changedProperties = new List<string?>();
        channel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        channel.SetAudioLevel(50, ChannelAudioDirection.Receive, streamId: 9);

        Assert.Equal([nameof(ChannelViewModel.AudioLevel), nameof(ChannelViewModel.AudioLevelScale)], changedProperties);
        Assert.DoesNotContain(nameof(ChannelViewModel.VolumeSliderValue), changedProperties);
    }

    private static ChannelViewModel CreateReceivingChannel(FneTrafficProtocol protocol, uint streamId)
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = protocol == FneTrafficProtocol.Dmr ? "dmr" : "p25",
            Slot = 1
        });
        channel.SetAudioEnabled(true);
        Assert.True(channel.TryApplyTraffic(
            "System 1",
            new FneTrafficFrame(
                protocol,
                peerId: 1,
                sourceId: 42,
                destinationId: 99,
                slot: protocol == FneTrafficProtocol.Dmr ? (byte)0 : null,
                callType: "GROUP",
                frameType: "VOICE",
                subtype: protocol == FneTrafficProtocol.Dmr ? "VOICE" : "LDU1",
                packetSequence: 1,
                streamId,
                payload: [])));
        return channel;
    }
}
