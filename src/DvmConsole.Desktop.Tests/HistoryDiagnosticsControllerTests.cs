// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class HistoryDiagnosticsControllerTests
{
    [Fact]
    public void EventCommandsProjectExportAndClearThroughOneController()
    {
        var history = new HistoryRecordingController("30", "recordings");
        using var debugLogs = CreateDebugLogs();
        var session = new TestSession { HasUiAccess = false };
        var controller = new HistoryDiagnosticsController(history, debugLogs, session);

        controller.AddEvent("Dispatch", "Console started", "100", "200");

        CallHistoryEntry entry = Assert.Single(history.CallHistory);
        Assert.True(entry.IsEvent);
        Assert.Equal("Dispatch", entry.EventSource);
        Assert.Equal("Console started", entry.EventMessage);
        Assert.Equal(1, session.PostCount);

        using var destination = new MemoryStream();
        controller.ExportHistory(destination, leaveOpen: true);
        string csv = Encoding.UTF8.GetString(destination.ToArray());

        Assert.Contains("Start,End,DurationSeconds,System,Channel", csv, StringComparison.Ordinal);
        Assert.Contains("\"Dispatch\"", csv, StringComparison.Ordinal);
        Assert.Contains("Exported 1 activity-history entry.", session.Status, StringComparison.Ordinal);

        controller.ClearHistory();

        Assert.Empty(history.CallHistory);
        Assert.Empty(history.FilteredCallHistory);
        Assert.Empty(history.ActivityCallHistory);
        Assert.Equal("Activity history cleared.", session.Status);
    }

    [Fact]
    public void DiagnosticCommandsKeepRedactionAndStatusInsideTheControllerBoundary()
    {
        var history = new HistoryRecordingController("30", "recordings");
        using var debugLogs = CreateDebugLogs();
        var session = new TestSession { HasUiAccess = true };
        var controller = new HistoryDiagnosticsController(history, debugLogs, session);

        controller.AddDebugLog(
            DateTimeOffset.UnixEpoch,
            "Example FNE",
            DebugLogSeverity.Warning,
            "password=secret");
        using var destination = new MemoryStream();
        controller.ExportDebugLogs(destination, "operator.tsv");
        string exported = Encoding.UTF8.GetString(destination.ToArray());

        Assert.DoesNotContain("password=secret", exported, StringComparison.Ordinal);
        Assert.Contains("[sensitive diagnostic message redacted]", exported, StringComparison.Ordinal);
        Assert.Equal("Exported 1 redacted debug log entry to operator.tsv.", session.Status);

        controller.ReportDebugExportFailure("disk unavailable");

        Assert.Equal("Unable to export debug logs: disk unavailable", session.Status);
    }

    private static DebugLogWorkspace CreateDebugLogs()
        => new(
            hasUiThreadAccess: () => true,
            postToUiThread: action => action(),
            isStopped: () => false);

    private sealed class TestSession : IHistoryDiagnosticsSession
    {
        public SystemViewModel? SelectedSystem { get; set; }
        public bool HasUiAccess { get; set; }
        public int PostCount { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public List<string> ChangedProperties { get; } = [];

        public bool CheckUiAccess() => HasUiAccess;

        public void PostToUi(Action action)
        {
            PostCount++;
            action();
        }

        public void PublishStatus(string text) => Status = text;
        public void NotifyPropertyChanged(string propertyName) => ChangedProperties.Add(propertyName);
    }
}
