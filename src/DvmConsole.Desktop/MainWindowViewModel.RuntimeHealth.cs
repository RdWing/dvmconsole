using DvmConsole.Operations;
using System.ComponentModel;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private readonly object runtimeHealthSync = new();
    private readonly FixedBucketLatencyTracker receiveLatencyHealth = new();
    private CatalogScanHealth recordingCatalogHealth = new(0, 0, 0, 0, 0, TimeSpan.Zero);
    private RecordingFinalizationSpoolHealth recordingFinalizationHealth = new(0, 0, null, null);
    private DateTimeOffset nextRecordingFinalizationHealthRefresh;
    private DateTimeOffset? transmitBacklogObservedAt;
    private int transmitPeakDepth;
    private int finalizationPeakDepth;
    private string? transmitHealthError;
    private int routeRecoveryAttempts;
    private TimeSpan? lastRouteRecoveryDuration;
    private string? lastRouteRecoveryResult;
    private PttActivationSource pttActivationSource;

    public string PttInputSourceText
        => pttActivationSource switch
        {
            PttActivationSource.LocalChannelControl => "local channel control",
            PttActivationSource.WindowLocalKeyboard => "window-local keyboard",
            PttActivationSource.OsGlobalKeyboard => "OS-global keyboard",
            PttActivationSource.SerialHardware => "serial hardware",
            _ => "no activation yet"
        };

    public IReadOnlyList<ChannelViewModel> RuntimeActiveTransmitChannels
        => transmitCoordinator.ActiveChannels;

    public string MicrophoneInputSourceText
    {
        get
        {
            if (SelectedAudioInputDevice is AudioDeviceOptionViewModel selected)
                return selected.DisplayName;
            return string.IsNullOrWhiteSpace(AudioInputDeviceIdText)
                ? "system default microphone"
                : "configured microphone";
        }
    }

    internal RuntimeHealthSnapshot CaptureRuntimeHealthSnapshot()
    {
        if (networkDisabledDemo && demoRuntimeHealthSnapshot is RuntimeHealthSnapshot demoSnapshot)
            return demoSnapshot with { CapturedAt = DateTimeOffset.UtcNow };

        ReceiveQueueHealth receive = Combine(
            receiveAudioWork.CaptureHealth(),
            patchSourceReceiveWork.CaptureHealth());
        MicrophoneHealth microphone = transmitCoordinator.MicrophoneHealth;
        int transmitDepth = transmitCoordinator.ActiveChannels.Count;
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        RecordingFinalizationSpoolHealth finalization = CaptureRecordingFinalizationHealth(capturedAt);
        DateTimeOffset? transmitStartedAt;
        int transmitPeak;
        int finalizationPeak;
        string? transmitError;
        CatalogScanHealth catalog;
        int recoveryAttempts;
        TimeSpan? recoveryDuration;
        string? recoveryResult;

        lock (runtimeHealthSync)
        {
            transmitPeakDepth = Math.Max(transmitPeakDepth, transmitDepth);
            finalizationPeakDepth = Math.Max(finalizationPeakDepth, finalization.PendingJobs);
            if (transmitDepth > 0)
                transmitBacklogObservedAt ??= capturedAt;
            else
                transmitBacklogObservedAt = null;

            transmitStartedAt = transmitBacklogObservedAt;
            transmitPeak = transmitPeakDepth;
            finalizationPeak = finalizationPeakDepth;
            transmitError = transmitHealthError;
            catalog = recordingCatalogHealth;
            recoveryAttempts = routeRecoveryAttempts;
            recoveryDuration = lastRouteRecoveryDuration;
            recoveryResult = lastRouteRecoveryResult;
        }

        string transmitStage = transmitDepth == 0
            ? "idle"
            : transmitCoordinator.IsMicrophoneAudioSuppressed
                ? "permit cue / microphone blocked"
                : "on air";
        string finalizationStage = finalization.QuarantinedJobs > 0
            ? $"{finalization.QuarantinedJobs} quarantined"
            : finalization.PendingJobs > 0
                ? "finalizing"
                : "idle";

        return new RuntimeHealthSnapshot(
            capturedAt,
            receive,
            microphone,
            new WorkBacklogHealth(
                transmitDepth,
                transmitPeak,
                transmitStartedAt is null ? null : capturedAt - transmitStartedAt.Value,
                transmitStage,
                transmitError ?? microphone.Fault),
            new WorkBacklogHealth(
                finalization.PendingJobs,
                finalizationPeak,
                finalization.OldestAge,
                finalizationStage,
                finalization.LastError),
            catalog,
            recoveryAttempts,
            recoveryDuration,
            recoveryResult,
            receiveLatencyHealth.Snapshot());
    }

    private void ObserveRuntimeReceiveTiming(ReceiveWorkItemTiming timing)
        => receiveLatencyHealth.Observe(timing.EndToEndDelay);

    private RecordingFinalizationSpoolHealth CaptureRecordingFinalizationHealth(
        DateTimeOffset capturedAt)
    {
        RecordingFinalizationSpoolHealth previous;
        lock (runtimeHealthSync)
        {
            if (capturedAt < nextRecordingFinalizationHealthRefresh)
                return recordingFinalizationHealth;
            nextRecordingFinalizationHealthRefresh = capturedAt.AddSeconds(2);
            previous = recordingFinalizationHealth;
        }

        RecordingFinalizationSpoolHealth observed;
        try
        {
            observed = callRecordings.FinalizationHealth;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            observed = new RecordingFinalizationSpoolHealth(
                previous.PendingJobs,
                previous.QuarantinedJobs,
                previous.OldestAge,
                exception.Message);
        }

        lock (runtimeHealthSync)
        {
            recordingFinalizationHealth = observed;
            return recordingFinalizationHealth;
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

    private void ObserveRecordingCatalogHealth(RecordingCatalogScanResult scan)
    {
        var health = new CatalogScanHealth(
            scan.ScannedFiles,
            scan.Recordings.Count,
            scan.PrunedFiles,
            scan.DamagedFiles,
            scan.InaccessiblePaths,
            scan.Duration);
        lock (runtimeHealthSync)
            recordingCatalogHealth = health;
    }

    private void ObservePttActivationSource(PttActivationSource source)
    {
        if (source == PttActivationSource.None || pttActivationSource == source)
            return;
        pttActivationSource = source;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PttInputSourceText)));
    }

    private void ObserveTransmitHealthError(Exception exception)
    {
        lock (runtimeHealthSync)
            transmitHealthError = exception.Message;
    }

    // Ownership hook for receive-output recovery without expanding the stable
    // Operations snapshot when a coordinator reports a result.
    private void ObserveRouteRecovery(TimeSpan duration, string result)
    {
        lock (runtimeHealthSync)
        {
            routeRecoveryAttempts = routeRecoveryAttempts == int.MaxValue
                ? int.MaxValue
                : routeRecoveryAttempts + 1;
            lastRouteRecoveryDuration = duration;
            lastRouteRecoveryResult = result;
        }
    }
}
