// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveIdleAllocationTests
{
    [Fact]
    public async Task ReadingChannelStateDoesNotAllocateProportionallyToChannelCount()
    {
        await using var audio = new ChannelReceiveAudioCoordinator(
            () => throw new InvalidOperationException("Idle inspection must not open audio."),
            () => throw new InvalidOperationException("Idle inspection must not open a vocoder."));
        await using var work = new ChannelReceiveWorkQueue((_, _) => Task.CompletedTask);
        using var gate = new SemaphoreSlim(1);

        ReceiveSessionPort CreatePort(int count) => new(
            audio, work, new ReceiveOutputMutePolicy(), gate,
            () => CreateChannels(count), () => false, _ => { }, _ => { });

        long small = MeasureStateReads(CreatePort(25), 25);
        long large = MeasureStateReads(CreatePort(1000), 1000);

        Assert.True(large <= small + 4096,
            $"100 state reads grew from {small} to {large} allocated bytes.");
    }

    [Fact]
    public void AdvancingIdleRoutesDoesNotAllocateProportionallyToRouteCount()
    {
        long Measure(int count)
        {
            var channels = CreateChannels(count);
            var router = new ReceiveAudioTrafficRouter(channels.ToDictionary(
                channel => (FneTrafficProtocol.P25, channel.Definition.DestinationId),
                channel => new[] { channel }));
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            for (int i = 0; i < 10; i++)
                Assert.Empty(router.Advance(now));

            long before = GC.GetAllocatedBytesForCurrentThread();
            int decisions = 0;
            for (int i = 0; i < 100; i++)
                decisions += router.Advance(now).Count;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, decisions);
            return allocated;
        }

        long small = Measure(25);
        long large = Measure(1000);

        Assert.True(large <= small + 4096,
            $"100 idle advances grew from {small} to {large} allocated bytes.");
    }

    private static long MeasureStateReads(ReceiveSessionPort port, int count)
    {
        for (int i = 0; i < 10; i++)
            foreach (ReceiveSessionChannelState state in port.Channels)
                Assert.False(state.AudioEnabled || state.RecordingEnabled);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int observed = 0;
        for (int i = 0; i < 100; i++)
            foreach (ReceiveSessionChannelState state in port.Channels)
                if (!state.AudioEnabled && !state.RecordingEnabled)
                    observed++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(count * 100, observed);
        return allocated;
    }

    private static ChannelViewModel[] CreateChannels(int count)
        => Enumerable.Range(1, count).Select(i => new ChannelViewModel(new ChannelConfiguration
        {
            Name = $"Channel {i}",
            System = "Test",
            Tgid = i.ToString(),
            Mode = "p25"
        })).ToArray();
}
