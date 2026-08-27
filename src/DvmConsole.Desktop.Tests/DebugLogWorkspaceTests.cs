using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DebugLogWorkspaceTests
{
    [Fact]
    public void OwnsIngestionFilteringAndRedactedExport()
    {
        using var workspace = new DebugLogWorkspace(
            hasUiThreadAccess: () => true,
            postToUiThread: action => action(),
            isStopped: () => false);
        var changedProperties = new List<string?>();
        workspace.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        DateTimeOffset timestamp = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

        workspace.Add(timestamp, "FNE", DebugLogSeverity.Info, "Connection established");
        workspace.Add(timestamp, "FNE", DebugLogSeverity.Warning, "password=secret");

        Assert.Equal(2, workspace.Entries.Count);
        Assert.Single(workspace.FilteredEntries);
        Assert.Equal("Connection established", workspace.FilteredEntries[0].Message);
        Assert.Contains(nameof(DebugLogWorkspace.RetentionText), changedProperties);

        workspace.SeverityFilter = "All";
        workspace.FilterText = "sensitive";
        Assert.Single(workspace.FilteredEntries);
        Assert.Equal("[sensitive diagnostic message redacted]", workspace.FilteredEntries[0].Message);

        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-debug-log-workspace-tests",
            Guid.NewGuid().ToString("N"));
        string exportPath = Path.Combine(root, "debug.tsv");
        try
        {
            Assert.Equal(2, workspace.Export(exportPath));
            string exported = File.ReadAllText(exportPath);
            Assert.Contains("Connection established", exported, StringComparison.Ordinal);
            Assert.Contains("[sensitive diagnostic message redacted]", exported, StringComparison.Ordinal);
            Assert.DoesNotContain("password=secret", exported, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
