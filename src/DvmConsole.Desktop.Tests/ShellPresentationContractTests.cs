using DvmConsole.Desktop;
using DvmConsole.FneClient;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ShellPresentationContractTests
{
    [Fact]
    public void SmokeResultOptionDoesNotBecomeTheConfigurationPath()
    {
        string[] arguments =
        [
            "--smoke-windows",
            "--smoke-result=/tmp/dvmconsole smoke.txt",
            "/tmp/codeplug.yml"
        ];

        Assert.Equal("/tmp/dvmconsole smoke.txt", Program.ReadOption(arguments, "--smoke-result="));
        Assert.Equal("/tmp/codeplug.yml", Program.ReadConfigurationPath(arguments));
    }

    [Theory]
    [InlineData(false, true, "/usr/bin/open", false)]
    [InlineData(true, false, "explorer.exe", false)]
    public void RevealRecordingUsesThePlatformFileManager(
        bool isWindows,
        bool isMacOS,
        string expectedExecutable,
        bool expectedShellExecution)
    {
        string path = Path.Combine(Path.GetTempPath(), "recording with spaces.wav");

        System.Diagnostics.ProcessStartInfo startInfo =
            MainWindowViewModel.CreateRevealRecordingStartInfo(path, isWindows, isMacOS);

        Assert.Equal(expectedExecutable, startInfo.FileName);
        Assert.Equal(expectedShellExecution, startInfo.UseShellExecute);
        Assert.NotEmpty(startInfo.ArgumentList);
        Assert.Equal(Path.GetFullPath(path), startInfo.ArgumentList[^1]);
    }

    [Fact]
    public void RevealRecordingOpensContainingFolderOnOtherPlatforms()
    {
        string folder = Path.Combine(Path.GetTempPath(), "recordings");
        string path = Path.Combine(folder, "call.wav");

        System.Diagnostics.ProcessStartInfo startInfo =
            MainWindowViewModel.CreateRevealRecordingStartInfo(path, isWindows: false, isMacOS: false);

        Assert.Equal(Path.GetFullPath(folder), startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Theory]
    [InlineData(400, 44, 2000, 600, 444)]
    [InlineData(1380, 44, 2000, 600, 1400)]
    [InlineData(20, -44, 2000, 600, 0)]
    [InlineData(100, 44, 500, 600, 0)]
    public void ScrollViewportAnchorOffsetTracksInsertedRowsAndClampsToBounds(
        double currentOffset,
        double itemDelta,
        double extentHeight,
        double viewportHeight,
        double expected)
        => Assert.Equal(
            expected,
            ScrollViewportAnchorMath.CalculateOffset(currentOffset, itemDelta, extentHeight, viewportHeight));

    [Fact]
    public void CallHistoryExposesCompactLocalDateBelowTheTime()
    {
        DateTimeOffset timestamp = new(2026, 8, 19, 21, 22, 23, TimeSpan.Zero);
        var entry = new CallHistoryEntry(
            timestamp,
            "Test",
            "Dispatch",
            1001,
            100,
            FneTrafficProtocol.P25,
            42);

        Assert.Equal(timestamp.ToLocalTime().ToString("HH:mm:ss"), entry.TimestampText);
        Assert.Equal(timestamp.ToLocalTime().ToString("yyyy-MM-dd"), entry.DateText);
    }

    [Theory]
    [InlineData(8_000, "1.0 s")]
    [InlineData(4_000, "0.5 s")]
    [InlineData(8_160, "1.02 s")]
    public void VocoderLevelDiagnosticsDescribeElapsedTime(int sampleCount, string expected)
        => Assert.Equal(expected, MainWindowViewModel.FormatAudioLevelDuration(sampleCount));

    [Fact]
    public void ActivityHistoryIncludesRecordingOnlyCatalogEntries()
    {
        var recordingOnly = new CallHistoryEntry(
            DateTimeOffset.UtcNow,
            "SKYNET",
            "CHP Maroon/Bronze",
            P25Defines.WUID_FNE,
            2947,
            FneTrafficProtocol.P25,
            77,
            isRecordingOnly: true);
        var otherSystem = new CallHistoryEntry(
            DateTimeOffset.UtcNow,
            "OTHER",
            "Dispatch",
            42,
            100,
            FneTrafficProtocol.Dmr,
            78);

        CallHistoryEntry[] selected = MainWindowViewModel.SelectActivityHistory(
            [recordingOnly, otherSystem],
            "SKYNET",
            selectedZoneChannelNames: null);

        Assert.Same(recordingOnly, Assert.Single(selected));
        Assert.True(selected[0].IsRecordingOnly);
    }

    [Fact]
    public void RecordingCatalogSnapshotRejectsStaleOrCancelledScans()
    {
        Assert.True(MainWindowViewModel.IsRecordingCatalogSnapshotCurrent(4, 4, 10, 10, false));
        Assert.False(MainWindowViewModel.IsRecordingCatalogSnapshotCurrent(4, 4, 10, 11, false));
        Assert.False(MainWindowViewModel.IsRecordingCatalogSnapshotCurrent(4, 5, 10, 10, false));
        Assert.False(MainWindowViewModel.IsRecordingCatalogSnapshotCurrent(4, 4, 10, 10, true));
    }

    [Fact]
    public void OperatorToolSectionCatalogCoversEachSectionInTabOrder()
    {
        OperatorToolSectionDefinition[] definitions = OperatorToolSectionCatalog.All.ToArray();
        Assert.Equal(Enum.GetValues<OperatorToolSection>(), definitions.Select(definition => definition.Section));
        Assert.Equal(definitions.Length, definitions.Select(definition => definition.CommandId).Distinct().Count());
        Assert.All(definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(definition.SearchTerms));
            Assert.StartsWith("settings.", definition.CommandId);
        });
    }
}
