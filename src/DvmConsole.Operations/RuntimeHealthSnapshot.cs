namespace DvmConsole.Operations;

public enum MicrophoneHealthState
{
    Stopped,
    Starting,
    Ready,
    Stale,
    Faulted
}

public sealed record ReceiveQueueHealth(
    int CurrentDepth,
    int PeakDepth,
    long CoalescedWakeCount,
    long SpuriousWakeCount);

public sealed record MicrophoneHealth(
    MicrophoneHealthState State,
    long CaptureGeneration,
    TimeSpan? LastSampleAge,
    TimeSpan? CallbackCadence,
    string? Fault);

public sealed record WorkBacklogHealth(
    int Depth,
    int PeakDepth,
    TimeSpan? OldestAge,
    string Stage,
    string? LastError);

public sealed record CatalogScanHealth(
    int FilesSeen,
    int Loaded,
    int Expired,
    int Damaged,
    int Inaccessible,
    TimeSpan Duration);

public sealed record LatencyPercentiles(
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan P99);

public sealed record RuntimeHealthSnapshot(
    DateTimeOffset CapturedAt,
    ReceiveQueueHealth ReceiveQueue,
    MicrophoneHealth Microphone,
    WorkBacklogHealth Transmit,
    WorkBacklogHealth RecordingFinalization,
    CatalogScanHealth RecordingCatalog,
    int RouteRecoveryAttempts,
    TimeSpan? LastRouteRecoveryDuration,
    string? LastRouteRecoveryResult,
    LatencyPercentiles ReceiveLatency);
