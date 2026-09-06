// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DebugLogWorkspaceTests
{
    [Fact]
    public void ExportRedactsIpv6AndKeepsMessagesInsideOneTsvField()
    {
        using var workspace = new DebugLogWorkspace(() => true, action => action(), () => false);
        workspace.Add(DateTimeOffset.UtcNow, "Transport", DebugLogSeverity.Warning,
            "Disconnected\t[2001:db8::12]:62031\r\nretry pending");
        using var stream = new MemoryStream();
        Assert.Equal(1, workspace.Export(stream));
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        string[] lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(lines[0].Split('\t').Length, lines[1].Split('\t').Length);
        Assert.DoesNotContain("2001:db8", lines[1], StringComparison.Ordinal);
        Assert.Contains("retry pending", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnsIngestionFilteringAndRedactedExport()
    {
        using var workspace = new DebugLogWorkspace(
            hasUiThreadAccess: () => true,
            postToUiThread: action => action(),
            isStopped: () => false);
        var changedProperties = new List<string?>();
        var published = new List<DebugLogEntry>();
        workspace.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        workspace.EntryPublished += (_, entry) => published.Add(entry);
        DateTimeOffset timestamp = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

        workspace.Add(timestamp, "Local20 FNE", DebugLogSeverity.Info, "Connection established");
        workspace.Add(timestamp, "FNE", DebugLogSeverity.Warning, "password=secret");
        workspace.Add(
            timestamp,
            "Skynet",
            DebugLogSeverity.Info,
            "(Skynet) peer 123 stream ID 42 via 192.0.2.1:62031");

        Assert.Equal(3, workspace.Entries.Count);
        Assert.Equal(3, published.Count);
        Assert.Equal("[sensitive diagnostic message redacted]", published[1].Message);
        Assert.Equal(2, workspace.FilteredEntries.Count);
        Assert.Contains(workspace.FilteredEntries, entry => entry.Message == "Connection established");
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
            Assert.Equal(3, workspace.Export(exportPath));
            string exported = File.ReadAllText(exportPath);
            Assert.Contains("Connection established", exported, StringComparison.Ordinal);
            Assert.Contains("[sensitive diagnostic message redacted]", exported, StringComparison.Ordinal);
            Assert.DoesNotContain("password=secret", exported, StringComparison.Ordinal);
            Assert.DoesNotContain("Local20 FNE", exported, StringComparison.Ordinal);
            Assert.DoesNotContain("Skynet", exported, StringComparison.Ordinal);
            Assert.DoesNotContain("192.0.2.1", exported, StringComparison.Ordinal);
            Assert.DoesNotContain("stream ID 42", exported, StringComparison.Ordinal);
            Assert.Contains("stream ID [redacted]", exported, StringComparison.Ordinal);
            Assert.Contains("Source 1", exported, StringComparison.Ordinal);

            using var stream = new MemoryStream(new byte[8_192]);
            Assert.Equal(3, workspace.Export(stream));
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

    [Theory]
    [InlineData(DebugLogSeverity.Debug, DvmConsole.Application.ConsoleLogLevel.Debug)]
    [InlineData(DebugLogSeverity.Info, DvmConsole.Application.ConsoleLogLevel.Information)]
    [InlineData(DebugLogSeverity.Warning, DvmConsole.Application.ConsoleLogLevel.Warning)]
    [InlineData(DebugLogSeverity.Error, DvmConsole.Application.ConsoleLogLevel.Error)]
    [InlineData(DebugLogSeverity.Fatal, DvmConsole.Application.ConsoleLogLevel.Error)]
    public void PortableSessionLogProjectionPreservesRedactedEntry(
        DebugLogSeverity severity,
        DvmConsole.Application.ConsoleLogLevel expectedLevel)
    {
        var entry = new DebugLogEntry(DateTimeOffset.UnixEpoch, "FNE", severity, "redacted message");

        DvmConsole.Application.ConsoleLogEvent projected =
            DesktopConsoleSessionRuntimeAdapter.ProjectLog(entry);

        Assert.Equal(entry.Timestamp, projected.Timestamp);
        Assert.Equal(expectedLevel, projected.Level);
        Assert.Equal(entry.Source, projected.Category);
        Assert.Equal(entry.Message, projected.Message);
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
