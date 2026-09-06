// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Desktop;
using DvmConsole.FneClient;
using System.Collections.Specialized;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class HistoryRecordingControllerTests
{
    [Fact]
    public void ActivityDatesFollowVisibleDayBoundariesWithoutReplacingRows()
    {
        var workspace = new HistoryRecordingController("30", "recordings");
        var today = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        CallHistoryEntry first = CreateHistoryEntry(1, today);
        CallHistoryEntry second = CreateHistoryEntry(2, today.AddMinutes(-1));
        CallHistoryEntry yesterday = CreateHistoryEntry(3, today.AddDays(-1));
        workspace.RefreshActivityCallHistory([first, second, yesterday]);
        Assert.Equal([true, false, true], workspace.ActivityCallHistory.Select(entry => entry.StartsActivityDay));

        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)workspace.ActivityCallHistory).CollectionChanged += (_, e) => changes.Add(e.Action);
        CallHistoryEntry newer = CreateHistoryEntry(4, today.AddMinutes(1));
        bool oldHeaderVisibleBeforeInsertion = false;
        workspace.ActivityCallHistoryChanging += (_, _) => oldHeaderVisibleBeforeInsertion = first.StartsActivityDay;
        workspace.RefreshActivityCallHistory([newer, first, second, yesterday]);
        Assert.True(oldHeaderVisibleBeforeInsertion);
        Assert.Equal([true, false, false, true], workspace.ActivityCallHistory.Select(entry => entry.StartsActivityDay));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Same(first, workspace.ActivityCallHistory[1]);

        workspace.RefreshActivityCallHistory([second, yesterday]);
        Assert.True(second.StartsActivityDay);
        Assert.True(yesterday.StartsActivityDay);
    }

    [Fact]
    public void ActivityHistorySignalsBeforeInsertingANewTopRow()
    {
        var workspace = new HistoryRecordingController("30", "recordings");
        CallHistoryEntry older = CreateHistoryEntry(1, DateTimeOffset.UtcNow.AddSeconds(-1));
        CallHistoryEntry newer = CreateHistoryEntry(2, DateTimeOffset.UtcNow);
        workspace.RefreshActivityCallHistory([older]);
        CallHistoryEntry? rowAtTopBeforeInsertion = null;
        int countBeforeInsertion = -1;
        NotifyCollectionChangedEventArgs? changing = null;
        workspace.ActivityCallHistoryChanging += (_, args) =>
        {
            changing = args;
            countBeforeInsertion = workspace.ActivityCallHistory.Count;
            rowAtTopBeforeInsertion = workspace.ActivityCallHistory[0];
        };

        workspace.RefreshActivityCallHistory([newer, older]);

        Assert.NotNull(changing);
        Assert.Equal(NotifyCollectionChangedAction.Add, changing.Action);
        Assert.Equal(0, changing.NewStartingIndex);
        Assert.Equal(1, countBeforeInsertion);
        Assert.Same(older, rowAtTopBeforeInsertion);
        Assert.Equal([newer, older], workspace.ActivityCallHistory);
    }

    [Fact]
    public void AdvancedFilterPreservesFacadeNotificationOrder()
    {
        var workspace = new HistoryRecordingController("30", "recordings");
        var changed = new List<string?>();
        workspace.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        workspace.RecordingProtocolFilter = "P25";

        Assert.Equal(
            [
                nameof(HistoryRecordingController.RecordingProtocolFilter),
                nameof(HistoryRecordingController.HasAdvancedHistoryFilters),
                nameof(HistoryRecordingController.HistoryFilterSummary)
            ],
            changed);
    }

    [Fact]
    public void CatalogMutationInvalidatesSnapshotAndRequestsRestart()
    {
        var workspace = new HistoryRecordingController("30", "recordings");
        RecordingCatalogScanSnapshot snapshot = workspace.BeginRecordingCatalogScan();
        bool applied = false;

        Assert.True(workspace.TryApplyRecordingCatalogSnapshot(snapshot, () => applied = true));
        Assert.True(applied);

        workspace.RecordRecordingCatalogMutation();

        Assert.False(workspace.TryApplyRecordingCatalogSnapshot(snapshot, () => applied = false));
        Assert.True(workspace.ShouldRestartRecordingCatalogScan(snapshot));
        RecordingCatalogScanShutdown shutdown = workspace.CancelRecordingCatalogScan();
        shutdown.Cancellation?.Dispose();
    }

    private static CallHistoryEntry CreateHistoryEntry(uint streamId, DateTimeOffset timestamp)
        => new(
            timestamp,
            "System",
            "Channel",
            sourceId: streamId,
            destinationId: 100,
            protocol: FneTrafficProtocol.P25,
            streamId: streamId);
}
