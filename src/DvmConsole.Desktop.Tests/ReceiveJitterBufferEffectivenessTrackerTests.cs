// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveJitterBufferEffectivenessTrackerTests
{
    [Fact]
    public void RetainsCompletedCallEvidencePerConnectionUntilReset()
    {
        var tracker = new ReceiveJitterBufferEffectivenessTracker();

        tracker.Observe("Alpha", Timing(reordered: true, deadlineMisses: 0));
        tracker.Observe("Alpha", Timing(reordered: false, deadlineMisses: 2));
        tracker.Observe("Beta", Timing(reordered: true, deadlineMisses: 1));

        Assert.Equal(new ReceiveJitterBufferEffectiveness(1, 2), tracker.GetSnapshot("Alpha"));
        Assert.Equal(new ReceiveJitterBufferEffectiveness(1, 1), tracker.GetSnapshot("Beta"));

        tracker.Reset("Alpha");

        Assert.Equal(default, tracker.GetSnapshot("Alpha"));
        Assert.Equal(new ReceiveJitterBufferEffectiveness(1, 1), tracker.GetSnapshot("Beta"));
    }

    [Fact]
    public void ConnectionLossResetsOnlyItsOwnLearningOnce()
    {
        var runtime = new ConsoleOperationalRuntime(new ConsoleSessionServices(), []);
        var alpha = SystemId.FromName("Alpha");
        Assert.True(runtime.ObserveConnectionState(alpha, "Alpha", RadioConnectionState.Connected).Changed);
        runtime.JitterEffectiveness.Observe("Alpha", Timing(true, 2));
        runtime.JitterEffectiveness.Observe("Beta", Timing(true, 1));
        var loss = runtime.ObserveConnectionState(alpha, "Alpha", RadioConnectionState.Disconnected);
        Assert.True(loss.LostConnection);
        Assert.Equal(default, runtime.JitterEffectiveness.GetSnapshot("Alpha"));
        Assert.Equal(new ReceiveJitterBufferEffectiveness(1, 1), runtime.JitterEffectiveness.GetSnapshot("Beta"));
        Assert.Equal(new ConsoleConnectionChange(false, false),
            runtime.ObserveConnectionState(alpha, "Alpha", RadioConnectionState.Disconnected));
        Assert.False(runtime.ObserveConnectionState(alpha, "Alpha", RadioConnectionState.Connected).LostConnection);
    }

    private static ReceiveWorkItemTiming Timing(bool reordered, int deadlineMisses)
        => new(
            new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                2,
                100,
                1,
                "GROUP",
                "VOICE",
                "VOICE",
                1,
                10,
                []),
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            JitterBufferReorderedPacket: reordered,
            JitterBufferDeadlineMissedPackets: deadlineMisses);
}
