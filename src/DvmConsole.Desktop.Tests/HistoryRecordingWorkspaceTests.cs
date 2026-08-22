using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class HistoryRecordingWorkspaceTests
{
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
                nameof(HistoryRecordingWorkspace.FilteredRecordings),
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
}
