// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingDisplayIdentityTests
{
    [Fact]
    public void DifferentIdsNeverFallBackToTheSamePath()
    {
        var left = new CallRecordingMetadata { RecordingId = Guid.NewGuid().ToString(), FilePath = "/same.wav" };
        var right = new CallRecordingMetadata { RecordingId = Guid.NewGuid().ToString(), FilePath = "/same.wav" };
        Assert.False(RecordingDisplayIdentity.Matches(left, right));
        Assert.False(RecordingDisplayIdentity.Matches(left, RecordingIdentity.Parse(right)!.Value, right.FilePath));
    }

    [Fact]
    public void MatchingIdDoesNotInspectInvalidOrMovedPaths()
    {
        RecordingId id = RecordingId.New();
        var left = new CallRecordingMetadata { RecordingId = id.ToString(), FilePath = "\0" };
        var right = new CallRecordingMetadata { RecordingId = id.ToString(), FilePath = "/moved.wav" };
        Assert.True(RecordingDisplayIdentity.Matches(left, right));
        Assert.True(RecordingDisplayIdentity.Matches(left, id, right.FilePath));
    }

    [Fact]
    public void LegacyRowsMatchNormalizedPathsWithoutRequiringFilesToExist()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var left = new CallRecordingMetadata { RecordingId = string.Empty, FilePath = Path.Combine(root, "child", "..", "call.wav") };
        var right = new CallRecordingMetadata { RecordingId = string.Empty, FilePath = Path.Combine(root, "call.wav") };
        Assert.True(RecordingDisplayIdentity.Matches(left, right));
        Assert.True(RecordingDisplayIdentity.Matches(left, RecordingId.New(), right.FilePath));
        Assert.False(RecordingDisplayIdentity.Matches(left, RecordingId.New(), "\0"));
    }
}
