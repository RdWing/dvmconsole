// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class DebugLogTests
{
    [Theory]
    [InlineData("connect [2001:db8::5]:62031 failed", "connect [endpoint] failed")]
    [InlineData("connect fe80::1%en0 failed", "connect [endpoint] failed")]
    [InlineData("connect ::1 failed", "connect [endpoint] failed")]
    [InlineData("connect https://operator:secret@example.invalid/api failed", "connect [endpoint] failed")]
    [InlineData("open '/Users/operator/Private Files/call.wav' failed", "open [path] failed")]
    [InlineData("open C:\\Users\\operator\\call.wav failed", "open [path] failed")]
    [InlineData("stop timed out at 12:34:56", "stop timed out at 12:34:56")]
    public void ExportRedactsEndpointsAndPathsWithoutLosingOperation(string message, string expected)
        => Assert.Equal(expected, DebugLogRedactor.RedactForExport(message));

    [Theory]
    [InlineData("Network Sent (to 127.0.0.1) -- DUMP 0000: secret", "[network payload redacted]")]
    [InlineData("Network Received raw packet secret", "[network payload redacted]")]
    [InlineData("password=secret", "[sensitive diagnostic message redacted]")]
    [InlineData("ordinary connection status", "ordinary connection status")]
    public void RedactsSensitiveDiagnosticContent(string message, string expected)
    {
        Assert.Equal(expected, DebugLogRedactor.Redact(message));
    }

    [Fact]
    public void ExportRedactionRemovesOperationalNetworkIdentifiers()
    {
        const string message = "(Skynet) peer 123 RID=456 TG 789 stream ID 42 via 192.0.2.1:62031";

        Assert.Equal(
            "(system) peer [redacted] RID [redacted] TG [redacted] stream ID [redacted] via [endpoint]",
            DebugLogRedactor.RedactForExport(message));
        Assert.Equal(message, DebugLogRedactor.Redact(message));
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
