// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ChannelReceiveStateTests
{
    [Fact]
    public void ReceiveOwnershipAcrossCopiesHonorsSelectionSuspensionAndResourceIdentity()
    {
        var first = new ConsoleChannelState(new ChannelRuntimeDefinition("First", "System", "dmr", 1, 0));
        var second = new ConsoleChannelState(new ChannelRuntimeDefinition("Second", "System", "dmr", 1, 0));
        var otherSlot = new ConsoleChannelState(new ChannelRuntimeDefinition("Other slot", "System", "dmr", 1, 1));
        ConsoleChannelState.LinkReceivePeers([first, second, otherSlot]);
        second.Operator.SetAudioEnabled(true);
        second.Runtime.MarkReceiving(42, 10);
        Assert.Null(first.ReceivePresentationOwner);
        first.Operator.SetAudioEnabled(true);
        Assert.Same(second, first.ReceivePresentationOwner);
        otherSlot.Operator.SetAudioEnabled(true);
        Assert.Null(otherSlot.ReceivePresentationOwner);
        second.Operator.SetAudioSuspended(true);
        Assert.Null(first.ReceivePresentationOwner);
        first.Runtime.MarkReceiving(99, 20);
        Assert.Same(first, first.ReceivePresentationOwner);
        Assert.Equal(99u, first.PresentedSourceId);
    }

    [Fact]
    public void LatePlaybackCompletionCannotClearTheCurrentAudibleStream()
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "System", "p25", 1, 0));
        ChannelReceiveState receive = channel.Receive;
        Assert.True(receive.TryBeginPlayback(42, 10));
        ReceivePlaybackIdentity previous = receive.Playback!;
        Assert.False(receive.TryBeginPlayback(99, 20));
        Assert.False(receive.EndPlayback(20));
        Assert.Equal(previous, receive.Playback);
        Assert.True(receive.EndPlayback(10));
        Assert.True(receive.TryBeginPlayback(99, 20));
        Assert.False(receive.EndPlayback(10));
        Assert.Equal(20u, receive.Playback!.StreamId);
        Assert.Equal(10u, previous.StreamId);
    }

    [Fact]
    public void MeterOwnershipIgnoresOtherStreamEndsAndClearsWithPlayback()
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "System", "p25", 1, 0));
        ChannelReceiveState receive = channel.Receive;
        receive.BeginMeter(10);
        receive.BeginMeter(20);
        receive.EndMeter(20);
        Assert.Equal(10, receive.MeterStreamId);
        receive.ClearPlayback();
        Assert.Equal(0, receive.MeterStreamId);
        receive.BeginMeter(20);
        receive.EndMeter(10);
        Assert.Equal(20, receive.MeterStreamId);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(42u)]
    [InlineData(uint.MaxValue)]
    public void CallerIdentitySurvivesIdleAndTransmitWithoutAPresentationAdapter(uint source)
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "System", "p25", 1, 0));
        Assert.Null(channel.Receive.LastCallerSource);
        channel.Runtime.MarkReceiving(source, 10);
        Assert.Equal(source, channel.Receive.LastCallerSource);
        channel.Runtime.MarkIdle();
        channel.Runtime.MarkTransmitting(20);
        channel.Runtime.MarkFault("Disconnected");
        Assert.Equal(source, channel.Receive.LastCallerSource);
        channel.Runtime.MarkReceiving(100, 30);
        Assert.Equal(100u, channel.Receive.LastCallerSource);
    }
}
