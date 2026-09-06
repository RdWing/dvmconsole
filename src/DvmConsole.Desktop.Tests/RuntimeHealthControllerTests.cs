// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Desktop;
using DvmConsole.Media;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RuntimeHealthControllerTests
{
    [Fact]
    public void CaptureAggregatesQueuesAndRetainsPeakAndObservedFailures()
    {
        var finalization = new FakeFinalizationHealthSource
        {
            Health = new RecordingFinalizationSpoolHealth(
                PendingJobs: 2,
                QuarantinedJobs: 0,
                OldestAge: TimeSpan.FromSeconds(3),
                LastError: null)
        };
        var controller = new RuntimeHealthController(finalization);
        DateTimeOffset startedAt = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        controller.ObserveTransmitError(new InvalidOperationException("send failed"));
        controller.ObserveRouteRecovery(TimeSpan.FromMilliseconds(25), "restored");
        RuntimeHealthSnapshot active = controller.Capture(CreateInput(
            startedAt,
            channelReceiveDepth: 2,
            patchReceiveDepth: 3,
            channelTransmitDepth: 1,
            patchTransmitDepth: 2,
            microphoneSuppressed: true));

        Assert.Equal(5, active.ReceiveQueue.CurrentDepth);
        Assert.Equal(3, active.Transmit.Depth);
        Assert.Equal(3, active.Transmit.PeakDepth);
        Assert.Equal("permit cue / microphone blocked", active.Transmit.Stage);
        Assert.Equal("send failed", active.Transmit.LastError);
        Assert.Equal(2, active.RecordingFinalization.Depth);
        Assert.Equal(1, active.RouteRecoveryAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(25), active.LastRouteRecoveryDuration);
        Assert.Equal("restored", active.LastRouteRecoveryResult);

        finalization.Health = finalization.Health with { PendingJobs = 0 };
        RuntimeHealthSnapshot idle = controller.Capture(CreateInput(
            startedAt.AddSeconds(3),
            channelReceiveDepth: 0,
            patchReceiveDepth: 0,
            channelTransmitDepth: 0,
            patchTransmitDepth: 0,
            microphoneSuppressed: false));

        Assert.Equal(3, idle.Transmit.PeakDepth);
        Assert.Equal("idle", idle.Transmit.Stage);
        Assert.Equal(2, idle.RecordingFinalization.PeakDepth);
    }

    [Fact]
    public void FinalizationHealthRefreshIsThrottledAndFailuresRetainLastCounts()
    {
        var source = new FakeFinalizationHealthSource
        {
            Health = new RecordingFinalizationSpoolHealth(4, 1, TimeSpan.FromSeconds(2), null)
        };
        var controller = new RuntimeHealthController(source);
        DateTimeOffset now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        RuntimeHealthSnapshot first = controller.Capture(CreateInput(now));
        source.Health = new RecordingFinalizationSpoolHealth(8, 0, null, null);
        RuntimeHealthSnapshot cached = controller.Capture(CreateInput(now.AddSeconds(1)));
        source.Failure = new IOException("spool unavailable");
        RuntimeHealthSnapshot failed = controller.Capture(CreateInput(now.AddSeconds(2)));

        Assert.Equal(2, source.ReadCount);
        Assert.Equal(4, first.RecordingFinalization.Depth);
        Assert.Equal(4, cached.RecordingFinalization.Depth);
        Assert.Equal(4, failed.RecordingFinalization.Depth);
        Assert.Equal("spool unavailable", failed.RecordingFinalization.LastError);
    }

    [Fact]
    public void PttInputSourcePublishesOnlyMeaningfulChanges()
    {
        var controller = new RuntimeHealthController(new FakeFinalizationHealthSource());

        Assert.Equal("no activation yet", controller.PttInputSourceText);
        Assert.False(controller.ObservePttActivationSource(PttActivationSource.None));
        Assert.True(controller.ObservePttActivationSource(PttActivationSource.OsGlobalKeyboard));
        Assert.False(controller.ObservePttActivationSource(PttActivationSource.OsGlobalKeyboard));
        Assert.Equal("OS-global keyboard", controller.PttInputSourceText);
    }

    private static RuntimeHealthCaptureInput CreateInput(
        DateTimeOffset capturedAt,
        int channelReceiveDepth = 0,
        int patchReceiveDepth = 0,
        int channelTransmitDepth = 0,
        int patchTransmitDepth = 0,
        bool microphoneSuppressed = false)
        => new(
            capturedAt,
            new ReceiveQueueHealth(channelReceiveDepth, channelReceiveDepth, 0, 0),
            new ReceiveQueueHealth(patchReceiveDepth, patchReceiveDepth, 0, 0),
            new MicrophoneHealth(
                MicrophoneHealthState.Ready,
                CaptureGeneration: 1,
                LastSampleAge: TimeSpan.Zero,
                CallbackCadence: TimeSpan.FromMilliseconds(20),
                Fault: null),
            new TransmitQueueHealth(channelTransmitDepth, channelTransmitDepth, null, 16),
            new TransmitQueueHealth(patchTransmitDepth, patchTransmitDepth, null, 16),
            microphoneSuppressed);

    private sealed class FakeFinalizationHealthSource : IRecordingFinalizationHealthSource
    {
        public RecordingFinalizationSpoolHealth Health { get; set; } = new(0, 0, null, null);
        public Exception? Failure { get; set; }
        public int ReadCount { get; private set; }

        public RecordingFinalizationSpoolHealth FinalizationHealth
        {
            get
            {
                ReadCount++;
                if (Failure is not null)
                    throw Failure;
                return Health;
            }
        }
    }
}
