using Avalonia.Controls;
using Avalonia;
using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DebugLogWindowTests
{
    [Fact]
    public void RecycledNullItemBuildsAnEmptyLogRow()
    {
        TextBlock row = DebugLogWindow.CreateLogRow(null);

        Assert.Equal(string.Empty, row.Text);
    }

    [Fact]
    public void LogItemBuildsItsSummaryRow()
    {
        var entry = new DebugLogEntry(
            DateTimeOffset.UnixEpoch,
            "Test",
            DebugLogSeverity.Info,
            "message");

        TextBlock row = DebugLogWindow.CreateLogRow(entry);

        Assert.Equal(entry.Summary, row.Text);
        Assert.Equal(new Thickness(0), row.Margin);
    }
}
