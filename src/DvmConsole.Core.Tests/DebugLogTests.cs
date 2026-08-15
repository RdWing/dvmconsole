using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class DebugLogTests
{
    [Theory]
    [InlineData("Network Sent (to 127.0.0.1) -- DUMP 0000: secret", "Network Sent (to 127.0.0.1) -- [payload redacted]")]
    [InlineData("Network Received raw packet secret", "[network payload redacted]")]
    [InlineData("password=secret", "[sensitive diagnostic message redacted]")]
    [InlineData("ordinary connection status", "ordinary connection status")]
    public void RedactsSensitiveDiagnosticContent(string message, string expected)
    {
        Assert.Equal(expected, DebugLogRedactor.Redact(message));
    }

    [Fact]
    public void FormatsStructuredEntryForOperatorView()
    {
        var entry = new DebugLogEntry(
            new DateTimeOffset(2026, 8, 15, 12, 34, 56, 789, TimeSpan.Zero),
            "Dispatch",
            DebugLogSeverity.Warning,
            "peer unavailable");

        Assert.Equal("WARNING", entry.SeverityText);
        Assert.Contains("Dispatch: peer unavailable", entry.Summary);
    }
}
