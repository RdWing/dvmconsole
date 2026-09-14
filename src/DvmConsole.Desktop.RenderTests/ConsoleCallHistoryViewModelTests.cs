// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class ConsoleCallHistoryViewModelTests
{
    [Fact]
    public void UpdatesActiveCallInPlaceAndReevaluatesSharedEncryptionFilter()
    {
        var model = new ConsoleCallHistoryViewModel();
        var call = Call();
        model.Refresh([call]);
        var row = Assert.Single(model.FilteredCallHistory);
        Assert.Equal("Active", row.DurationText);
        int changes = 0;
        row.PropertyChanged += (_, _) => changes++;
        model.Refresh([call]);
        Assert.Equal(0, changes);
        model.Refresh([call with { EndedAt = call.StartedAt.AddSeconds(3) }]);
        Assert.Same(row, Assert.Single(model.FilteredCallHistory));
        Assert.Equal("3.0s", row.DurationText);
        model.RecordingEncryptionFilter = "Encrypted";
        Assert.Empty(model.FilteredCallHistory);
        model.Refresh([call with { Encryption = RecordingEncryptionDescriptor.Secure(0x84, 1) }]);
        Assert.Same(row, Assert.Single(model.FilteredCallHistory));
        Assert.False(row.HasPlayableRecording);
    }

    [Fact]
    public void SharesSearchAndStructuredFiltersAndRemovesRetiredRecords()
    {
        var model = new ConsoleCallHistoryViewModel();
        var first = Call();
        var second = first with { Id = CallId.New(), ChannelName = "Operations", SourceId = 77, Caller = "Engine 77" };
        model.Refresh([first, second]);
        model.CallHistoryFilterText = "engine";
        Assert.Equal(second.Id, Assert.Single(model.FilteredCallHistory).Record.Id);
        model.RecordingTalkgroupFilterText = "999";
        Assert.Empty(model.FilteredCallHistory);
        model.ClearHistoryFilters();
        Assert.Equal(2, model.FilteredCallHistory.Count);
        model.Refresh([]);
        Assert.Empty(model.FilteredCallHistory);
    }

    [Fact]
    public void AttachesClosestCallAndTracksPlaybackAndDeletionWithoutReplacingItsRow()
    {
        var call = Call();
        var other = call with { Id = CallId.New(), StartedAt = call.StartedAt.AddSeconds(4) };
        var recording = new RecordingArchiveEntry(new RecordingId(Guid.NewGuid()), call.StartedAt,
            TimeSpan.FromSeconds(2), call.SystemName, call.ChannelName, "RX", "P25", "1001", "2001",
            "Unit 1001", "1001 → TG 2001", [10], CallRecordingEncryptionState.Clear, "Clear", true, "call.opus", "TAR")
        { CallIdentity = RecordingCallIdentity.FromCall(call) };
        IReadOnlyList<RecordingArchiveEntry> catalog = [recording];
        var model = new ConsoleCallHistoryViewModel();
        model.Refresh([other, call], catalog, _ => false);
        var rows = model.FilteredCallHistory.ToArray();
        Assert.False(rows[0].HasRecording);
        Assert.Equal(recording, rows[1].Recording);
        Assert.True(rows[1].HasPlayableRecording);
        model.Refresh([other, call], catalog, _ => true);
        Assert.Equal("Stop", rows[1].RecordingPlaybackActionText);
        model.Refresh([other, call], catalog, _ => false);
        Assert.Equal("Play", rows[1].RecordingPlaybackActionText);
        model.Refresh([other, call], []);
        Assert.Same(rows[1], model.FilteredCallHistory[1]);
        Assert.False(rows[1].HasRecording);
    }

    [Fact]
    public void UnifiedTimelineRetainsArchivesWithoutDuplicatingAttachedCalls()
    {
        var call = Call();
        var recording = new RecordingArchiveEntry(new RecordingId(Guid.NewGuid()), call.StartedAt,
            TimeSpan.FromSeconds(2), call.SystemName, call.ChannelName, "RX", "P25", "1001", "2001",
            "Unit 1001", "1001 → TG 2001", [10], CallRecordingEncryptionState.Clear, "Clear", true, "call.opus", "TAR")
        { CallIdentity = RecordingCallIdentity.FromCall(call) };
        var older = recording with { Id = new RecordingId(Guid.NewGuid()), StartedAt = call.StartedAt.AddDays(-1), CallIdentity = null };
        IReadOnlyList<RecordingArchiveEntry> catalog = [older, recording];
        var model = new UnifiedCallHistoryViewModel();
        model.Refresh([call], catalog, _ => false);
        Assert.Equal(2, model.FilteredCallHistory.Count);
        Assert.Equal(recording, Assert.IsType<ConsoleCallHistoryItemViewModel>(model.FilteredCallHistory[0]).Recording);
        var archivedRow = Assert.IsType<RecordingHistoryItemViewModel>(model.FilteredCallHistory[1]);
        model.Refresh([call], catalog, _ => true);
        Assert.Same(archivedRow, model.FilteredCallHistory[1]);
        Assert.True(archivedRow.IsRecordingPlaying);
        model.Refresh([], catalog, _ => false);
        Assert.Equal(2, model.FilteredCallHistory.Count);
        Assert.All(model.FilteredCallHistory, row => Assert.IsType<RecordingHistoryItemViewModel>(row));
        model.Refresh([], [older], _ => false);
        Assert.Same(archivedRow, Assert.Single(model.FilteredCallHistory));
    }

    private static ConsoleCallHistoryRecord Call() => new(CallId.New(), DateTimeOffset.UnixEpoch, null,
        SystemId.FromName("North"), "North", null, "Dispatch", RadioMediaProtocol.P25, 1001, 2001,
        10, [10], null, "Unit 1001", ConsoleCallDirection.Receive, RecordingEncryptionDescriptor.Clear,
        "", "", "", "");
}
