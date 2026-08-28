using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DebugLogWorkspaceTests
{
    [Fact]
    public async Task OwnsIngestionFilteringAndRedactedExport()
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
        await WaitForAsync(() => workspace.FilteredEntries.Count == 1);
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

            using var stream = new MemoryStream(new byte[8_192]);
            Assert.Equal(2, workspace.Export(stream));
            Assert.True(stream.Length < 8_192);
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            string streamed = reader.ReadToEnd();
            Assert.Contains("Connection established", streamed, StringComparison.Ordinal);
            Assert.DoesNotContain("password=secret", streamed, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SearchFilteringAppliesOnlyTheLatestDebouncedText()
    {
        var delays = new List<TaskCompletionSource>();
        using var workspace = new DebugLogWorkspace(
            hasUiThreadAccess: () => true,
            postToUiThread: action => action(),
            isStopped: () => false,
            filterDebounceInterval: TimeSpan.FromSeconds(1),
            debounceDelayAsync: (_, cancellationToken) =>
            {
                var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationToken.Register(() => delay.TrySetCanceled(cancellationToken));
                delays.Add(delay);
                return delay.Task;
            });
        DateTimeOffset timestamp = DateTimeOffset.UnixEpoch;
        workspace.Add(timestamp, "FNE", DebugLogSeverity.Info, "alpha");
        workspace.Add(timestamp, "FNE", DebugLogSeverity.Info, "beta");

        workspace.FilterText = "alpha";
        workspace.FilterText = "beta";

        Assert.Equal(2, delays.Count);
        Assert.True(delays[0].Task.IsCanceled);
        Assert.Equal(2, workspace.FilteredEntries.Count);

        delays[1].TrySetResult();
        await WaitForAsync(() => workspace.FilteredEntries.Count == 1);

        Assert.Equal("beta", workspace.FilteredEntries[0].Message);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for the debug-log workspace state.");
            await Task.Delay(10);
        }
    }
}
