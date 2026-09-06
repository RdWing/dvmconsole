// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Media;
using DvmConsole.Operations;

namespace DvmConsole.Desktop;

internal interface IRecordingFinalizationHealthSource
{
    RecordingFinalizationSpoolHealth FinalizationHealth { get; }
}

internal sealed record RuntimeHealthCaptureInput(
    DateTimeOffset CapturedAt,
    ReceiveQueueHealth ChannelReceive,
    ReceiveQueueHealth PatchReceive,
    MicrophoneHealth Microphone,
    TransmitQueueHealth ChannelTransmit,
    TransmitQueueHealth PatchTransmit,
    bool IsMicrophoneAudioSuppressed);

/// <summary>
/// Owns rolling engineering-health state. Runtime services supply immutable
/// point-in-time values, leaving aggregation, peak tracking, refresh
/// throttling, and input-source presentation outside the shell facade.
/// </summary>
internal sealed class RuntimeHealthController
{
    private readonly IRecordingFinalizationHealthSource recordingFinalization;
    private readonly object sync = new();
    private readonly FixedBucketLatencyTracker receiveLatency = new();
    private CatalogScanHealth recordingCatalog = new(0, 0, 0, 0, 0, TimeSpan.Zero);
    private RecordingFinalizationSpoolHealth finalization = new(0, 0, null, null);
    private DateTimeOffset nextFinalizationRefresh;
    private DateTimeOffset? transmitBacklogObservedAt;
    private int transmitPeakDepth;
    private int finalizationPeakDepth;
    private string? transmitError;
    private int routeRecoveryAttempts;
    private TimeSpan? lastRouteRecoveryDuration;
    private string? lastRouteRecoveryResult;
    private PttActivationSource pttActivationSource;

    public RuntimeHealthController(IRecordingFinalizationHealthSource recordingFinalization)
    {
        this.recordingFinalization = recordingFinalization ??
            throw new ArgumentNullException(nameof(recordingFinalization));
    }

    public string PttInputSourceText
        => pttActivationSource switch
        {
            PttActivationSource.LocalChannelControl => "local channel control",
            PttActivationSource.WindowLocalKeyboard => "window-local keyboard",
            PttActivationSource.OsGlobalKeyboard => "OS-global keyboard",
            PttActivationSource.SerialHardware => "serial hardware",
            _ => "no activation yet"
        };

    public RuntimeHealthSnapshot Capture(RuntimeHealthCaptureInput input)
    {
        ReceiveQueueHealth receive = Combine(input.ChannelReceive, input.PatchReceive);
        int transmitDepth = input.ChannelTransmit.Depth + input.PatchTransmit.Depth;
        int measuredTransmitPeak = input.ChannelTransmit.PeakDepth + input.PatchTransmit.PeakDepth;
        TimeSpan? oldestTransmitAge = Max(
            input.ChannelTransmit.OldestAge,
            input.PatchTransmit.OldestAge);
        RecordingFinalizationSpoolHealth observedFinalization =
            CaptureFinalizationHealth(input.CapturedAt);
        DateTimeOffset? transmitStartedAt;
        int transmitPeak;
        int finalizationPeak;
        string? observedTransmitError;
        CatalogScanHealth observedCatalog;
        int recoveryAttempts;
        TimeSpan? recoveryDuration;
        string? recoveryResult;

        lock (sync)
        {
            transmitPeakDepth = Math.Max(
                transmitPeakDepth,
                Math.Max(transmitDepth, measuredTransmitPeak));
            finalizationPeakDepth = Math.Max(
                finalizationPeakDepth,
                observedFinalization.PendingJobs);
            if (transmitDepth > 0)
                transmitBacklogObservedAt ??= input.CapturedAt;
            else
                transmitBacklogObservedAt = null;

            transmitStartedAt = transmitBacklogObservedAt;
            transmitPeak = transmitPeakDepth;
            finalizationPeak = finalizationPeakDepth;
            observedTransmitError = transmitError;
            observedCatalog = recordingCatalog;
            recoveryAttempts = routeRecoveryAttempts;
            recoveryDuration = lastRouteRecoveryDuration;
            recoveryResult = lastRouteRecoveryResult;
        }

        string transmitStage = transmitDepth == 0
            ? "idle"
            : input.IsMicrophoneAudioSuppressed
                ? "permit cue / microphone blocked"
                : "on air";
        string finalizationStage = observedFinalization.QuarantinedJobs > 0
            ? $"{observedFinalization.QuarantinedJobs} quarantined"
            : observedFinalization.PendingJobs > 0
                ? "finalizing"
                : "idle";

        return new RuntimeHealthSnapshot(
            input.CapturedAt,
            receive,
            input.Microphone,
            new WorkBacklogHealth(
                transmitDepth,
                transmitPeak,
                oldestTransmitAge ??
                    (transmitStartedAt is null ? null : input.CapturedAt - transmitStartedAt.Value),
                transmitStage,
                observedTransmitError ?? input.Microphone.Fault),
            new WorkBacklogHealth(
                observedFinalization.PendingJobs,
                finalizationPeak,
                observedFinalization.OldestAge,
                finalizationStage,
                observedFinalization.LastError),
            observedCatalog,
            recoveryAttempts,
            recoveryDuration,
            recoveryResult,
            receiveLatency.Snapshot());
    }

    public void ObserveReceiveTiming(ReceiveWorkItemTiming timing)
        => receiveLatency.Observe(timing.EndToEndDelay);

    public void ObserveRecordingCatalog(RecordingCatalogScanResult scan)
    {
        var health = new CatalogScanHealth(
            scan.ScannedFiles,
            scan.Recordings.Count,
            scan.PrunedFiles,
            scan.DamagedFiles,
            scan.InaccessiblePaths,
            scan.Duration);
        lock (sync)
            recordingCatalog = health;
    }

    public bool ObservePttActivationSource(PttActivationSource source)
    {
        if (source == PttActivationSource.None || pttActivationSource == source)
            return false;

        pttActivationSource = source;
        return true;
    }

    public void ObserveTransmitError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (sync)
            transmitError = exception.Message;
    }

    public void ObserveRouteRecovery(TimeSpan duration, string result)
    {
        lock (sync)
        {
            routeRecoveryAttempts = routeRecoveryAttempts == int.MaxValue
                ? int.MaxValue
                : routeRecoveryAttempts + 1;
            lastRouteRecoveryDuration = duration;
            lastRouteRecoveryResult = result;
        }
    }

    private RecordingFinalizationSpoolHealth CaptureFinalizationHealth(DateTimeOffset capturedAt)
    {
        RecordingFinalizationSpoolHealth previous;
        lock (sync)
        {
            if (capturedAt < nextFinalizationRefresh)
                return finalization;
            nextFinalizationRefresh = capturedAt.AddSeconds(2);
            previous = finalization;
        }

        RecordingFinalizationSpoolHealth observed;
        try
        {
            observed = recordingFinalization.FinalizationHealth;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            observed = new RecordingFinalizationSpoolHealth(
                previous.PendingJobs,
                previous.QuarantinedJobs,
                previous.OldestAge,
                exception.Message);
        }

        lock (sync)
        {
            finalization = observed;
            return finalization;
        }
    }

    private static ReceiveQueueHealth Combine(
        ReceiveQueueHealth left,
        ReceiveQueueHealth right)
        => new(
            SaturatingAdd(left.CurrentDepth, right.CurrentDepth),
            SaturatingAdd(left.PeakDepth, right.PeakDepth),
            SaturatingAdd(left.CoalescedWakeCount, right.CoalescedWakeCount),
            SaturatingAdd(left.SpuriousWakeCount, right.SpuriousWakeCount));

    private static int SaturatingAdd(int left, int right)
        => left > int.MaxValue - right ? int.MaxValue : left + right;

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static TimeSpan? Max(TimeSpan? first, TimeSpan? second)
        => (first, second) switch
        {
            (null, null) => null,
            (TimeSpan value, null) => value,
            (null, TimeSpan value) => value,
            (TimeSpan left, TimeSpan right) => left >= right ? left : right
        };
}
