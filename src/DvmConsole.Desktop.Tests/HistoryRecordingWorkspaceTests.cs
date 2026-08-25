using DvmConsole.Desktop;
using DvmConsole.FneClient;
using System.Collections.Specialized;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class HistoryRecordingWorkspaceTests
{
    [Fact]
    public void ActivityHistorySignalsBeforeInsertingANewTopRow()
    {
        var workspace = new HistoryRecordingWorkspace("30", "recordings");
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
        var workspace = new HistoryRecordingWorkspace("30", "recordings");
        var changed = new List<string?>();
        workspace.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        workspace.RecordingProtocolFilter = "P25";

        Assert.Equal(
            [
                nameof(HistoryRecordingWorkspace.RecordingProtocolFilter),
                nameof(HistoryRecordingWorkspace.HasAdvancedHistoryFilters),
                nameof(HistoryRecordingWorkspace.HistoryFilterSummary)
            ],
            changed);
    }

    [Fact]
    public void CatalogMutationInvalidatesSnapshotAndRequestsRestart()
    {
        var workspace = new HistoryRecordingWorkspace("30", "recordings");
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
