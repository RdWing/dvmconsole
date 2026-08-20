using DvmConsole.Core.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DebugLogSearchTests
{
    private static readonly DebugLogEntry Entry = new(
        DateTimeOffset.UnixEpoch,
        "San Francisco",
        DebugLogSeverity.Debug,
        "Vocoder RX P25 on Fire/EMS: peak -4.2 dBFS over 1.0 s, stream 77.");

    [Theory]
    [InlineData("")]
    [InlineData("San Francisco")]
    [InlineData("Fire/EMS peak")]
    [InlineData("stream 77 P25 San")]
    [InlineData("  FIRE/ems   -4.2\tRX ")]
    public void EveryEnteredTermMayMatchAnywhereInTheLogEntry(string searchText)
        => Assert.True(DebugLogSearch.Matches(Entry, searchText));

    [Theory]
    [InlineData("San Oakland")]
    [InlineData("stream 78")]
    public void OneMissingTermRejectsTheLogEntry(string searchText)
        => Assert.False(DebugLogSearch.Matches(Entry, searchText));
}
