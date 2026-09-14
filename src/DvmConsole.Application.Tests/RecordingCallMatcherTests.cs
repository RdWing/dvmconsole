// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class RecordingCallMatcherTests
{
    private static RecordingCallIdentity Identity => new(DateTimeOffset.UnixEpoch,
        "System", "Channel", "RX", "P25", 42, 100, 77, null);

    [Fact]
    public void EpisodeMatchesAcrossPhysicalStreamsAndDuplicateChannelLabels()
    {
        var recording = Identity with { ReceiveEpisodeId = 9 };
        var call = recording with { ChannelName = "Other copy", StreamId = 88, StartedAt = recording.StartedAt.AddSeconds(-8) };
        Assert.True(RecordingCallMatcher.Matches(recording, call));
        Assert.False(RecordingCallMatcher.Matches(recording, call with { ReceiveEpisodeId = 10 }));
    }

    [Fact]
    public void ReusedEpisodeDoesNotAttachMediaFromAnotherCallLifetime()
    {
        var recording = Identity with { ReceiveEpisodeId = 1 };
        Assert.False(RecordingCallMatcher.Matches(recording,
            recording with { StartedAt = recording.StartedAt.AddDays(1) }));
        Assert.False(RecordingCallMatcher.Matches(recording,
            recording with { StartedAt = recording.StartedAt.AddDays(-1), EndedAt = recording.StartedAt.AddHours(-1) }));
        Assert.True(RecordingCallMatcher.Matches(recording,
            recording with { StartedAt = recording.StartedAt.AddMinutes(-1), EndedAt = recording.StartedAt.AddSeconds(2) }));
    }

    [Fact]
    public void LegacyStreamMatchRetainsFiveSecondBoundaryAndOptionalSubscriber()
    {
        var recording = Identity with { SourceId = null };
        Assert.True(RecordingCallMatcher.Matches(recording, Identity with { StartedAt = Identity.StartedAt.AddSeconds(5) }));
        Assert.False(RecordingCallMatcher.Matches(recording, Identity with { StartedAt = Identity.StartedAt.AddSeconds(5).AddTicks(1) }));
        Assert.False(RecordingCallMatcher.Matches(recording, Identity with { StreamId = 88 }));
        Assert.False(RecordingCallMatcher.Matches(recording, Identity with { ChannelName = "Other" }));
    }

    [Fact]
    public void IndexKeepsClosestCallAndStableTiesWithinTheSameEpisodeRoute()
    {
        var identity = Identity with { ReceiveEpisodeId = 9 };
        var farther = new Candidate(identity with { StartedAt = identity.StartedAt.AddSeconds(-2) });
        var closest = new Candidate(identity with { StreamId = 88, ChannelName = "Other copy" });
        var tied = new Candidate(identity);
        var unrelated = new Candidate(identity with { SystemName = "Other system" });
        var index = new RecordingCallIndex<Candidate>([farther, closest, tied, unrelated], item => item.Identity);
        Assert.Same(closest, index.FindBest(identity, out int visited));
        Assert.Equal(3, visited);
        Assert.Same(unrelated, index.FindBest(unrelated.Identity, out visited));
        Assert.Equal(1, visited);
    }

    private sealed record Candidate(RecordingCallIdentity Identity);

    [Theory]
    [InlineData("system")]
    [InlineData("protocol")]
    [InlineData("direction")]
    [InlineData("source")]
    [InlineData("destination")]
    public void EpisodeIdentityStillRequiresTheSameRoute(string field)
    {
        var recording = Identity with { ReceiveEpisodeId = 9 };
        var call = field switch
        {
            "system" => recording with { SystemName = "Other" },
            "protocol" => recording with { Protocol = "DMR" },
            "direction" => recording with { Direction = "TX" },
            "source" => recording with { SourceId = 43 },
            _ => recording with { DestinationId = 101 }
        };
        Assert.False(RecordingCallMatcher.Matches(recording, call));
    }
}
