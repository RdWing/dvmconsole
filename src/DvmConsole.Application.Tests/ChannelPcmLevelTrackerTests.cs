// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ChannelPcmLevelTrackerTests
{
    [Fact]
    public void KeepsDirectionsIndependentAndDropsPartialWindowsOnStreamReplacementOrClear()
    {
        var channel = ConsoleChannelState.GetId(new ChannelRuntimeDefinition("Dispatch", "Test", "p25", 100, 0));
        var tracker = new ChannelPcmLevelTracker(4);
        Assert.Empty(tracker.Observe(channel, ChannelAudioDirection.Receive, 10, [1, 1]));
        Assert.Empty(tracker.Observe(channel, ChannelAudioDirection.Transmit, 20, [1, 1]));
        Assert.Empty(tracker.Observe(channel, ChannelAudioDirection.Receive, 11, [2, 2]));
        Assert.Single(tracker.Observe(channel, ChannelAudioDirection.Transmit, 20, [1, 1]));
        Assert.Single(tracker.Observe(channel, ChannelAudioDirection.Receive, 11, [2, 2]));
        Assert.Empty(tracker.Observe(channel, ChannelAudioDirection.Receive, 11, [1, 1]));
        tracker.Clear();
        Assert.Empty(tracker.Observe(channel, ChannelAudioDirection.Receive, 11, [1, 1]));
    }
}
