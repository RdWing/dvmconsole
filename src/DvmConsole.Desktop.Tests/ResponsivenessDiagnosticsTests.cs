// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ResponsivenessDiagnosticsTests
{
    [Fact]
    public void UiWarningsAreThresholdedAndThrottledIndependentlyByCategory()
    {
        var clock = new ManualClock();
        var reports = new List<UiLatencyObservation>();
        var reporter = new UiLatencyReporter(reports.Add, clock);
        TimeSpan slow = TimeSpan.FromMilliseconds(200);
        reporter.Observe(UiLatencyCategory.ReceiveActivity, TimeSpan.FromMilliseconds(199), TimeSpan.Zero);
        Assert.Empty(reports);
        reporter.Observe(UiLatencyCategory.ReceiveActivity, slow, TimeSpan.Zero);
        for (int i = 0; i < 1000; i++)
            reporter.Observe(UiLatencyCategory.ReceiveActivity, slow, slow);
        Assert.Single(reports);
        reporter.Observe(UiLatencyCategory.RecordingPlay, TimeSpan.Zero, slow);
        reporter.Observe(UiLatencyCategory.RecordingStop, slow, TimeSpan.Zero);
        Assert.Equal(3, reports.Count);
        clock.Advance(TimeSpan.FromSeconds(5));
        reporter.Observe(UiLatencyCategory.ReceiveActivity, slow, TimeSpan.Zero);
        Assert.Equal(4, reports.Count);
    }

    [Fact]
    public void BrokenDiagnosticSinkCannotInterruptPresentation()
    {
        var reporter = new UiLatencyReporter(_ => throw new InvalidOperationException());
        reporter.Observe(UiLatencyCategory.RecordingPlay, TimeSpan.FromSeconds(1), TimeSpan.Zero);
    }

    [Fact]
    public void SelectionAuditRecordsActualChangesAndOriginWithoutReportingSuspensionAsDeselection()
    {
        var channel = CreateChannel();
        var changes = new List<ChannelSelectionChange>();
        channel.SelectionChanged += (_, change) => changes.Add(change);
        channel.SetAudioEnabled(true, "operator RX start");
        channel.SetAudioSuspended(true);
        channel.SetAudioEnabled(true);
        Assert.Single(changes);
        channel.SetAudioEnabled(false, "operator RX stop");
        channel.RestoreRecordingEnabled(true);
        channel.SetRecordingEnabled(true);
        channel.SetRecordingEnabled(false, "operator TAR stop");
        Assert.Equal(4, changes.Count);
        Assert.Equal(new(ChannelSelectionKind.Receive, false, true, "operator RX start"), changes[0]);
        Assert.Equal(new(ChannelSelectionKind.Receive, true, false, "operator RX stop"), changes[1]);
        Assert.StartsWith("settings restore:", changes[2].Origin);
        Assert.Equal(new(ChannelSelectionKind.Recording, true, false, "operator TAR stop"), changes[3]);
    }

    [Fact]
    public void BrokenSelectionObserverCannotPreventUiOrRecordingNotifications()
    {
        var channel = CreateChannel();
        channel.SelectionChanged += (_, _) => throw new InvalidOperationException();
        int notifications = 0;
        channel.RecordingStateChanged += (_, _) => notifications++;
        channel.SetAudioEnabled(true);
        channel.SetRecordingEnabled(true);
        Assert.True(channel.IsAudioEnabled);
        Assert.True(channel.IsRecordingEnabled);
        Assert.Equal(1, notifications);
    }

    private static ChannelViewModel CreateChannel() => new(new ChannelConfiguration
    {
        Name = "Dispatch",
        System = "Test",
        Tgid = "100",
        Mode = "p25"
    });

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
}
