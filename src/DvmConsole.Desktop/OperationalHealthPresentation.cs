using DvmConsole.Operations;
using System.Globalization;

namespace DvmConsole.Desktop;

internal static class OperationalHealthPresentation
{
    public static string FormatMicrophone(MicrophoneHealth health, bool blocked)
    {
        ArgumentNullException.ThrowIfNull(health);
        return health.State switch
        {
            MicrophoneHealthState.Faulted =>
                $"Mic: FAULTED{FormatDiagnostic(health.Fault)}",
            MicrophoneHealthState.Stale =>
                $"Mic: STALE{FormatAge(health.LastSampleAge)}",
            MicrophoneHealthState.Ready when blocked =>
                $"Mic: BLOCKED (permit cue){FormatAge(health.LastSampleAge)}",
            MicrophoneHealthState.Ready =>
                $"Mic: READY{FormatAge(health.LastSampleAge)}",
            MicrophoneHealthState.Starting => "Mic: STARTING",
            _ => "Mic: IDLE (opens on PTT)"
        };
    }

    public static string FormatMicrophoneEngineering(MicrophoneHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        string cadence = health.CallbackCadence is TimeSpan value
            ? $" · cadence {FormatDuration(value)}"
            : string.Empty;
        return $"{FormatMicrophone(health, blocked: false)} · generation {health.CaptureGeneration:N0}{cadence}";
    }

    public static string FormatReceiveQueue(ReceiveQueueHealth health)
        => $"RX queue {health.CurrentDepth} now / {health.PeakDepth} peak · " +
           $"{health.CoalescedWakeCount:N0} wakes coalesced";

    public static string FormatWorkBacklog(string label, WorkBacklogHealth health)
    {
        string age = health.OldestAge is TimeSpan oldest
            ? $" · oldest {FormatDuration(oldest)}"
            : string.Empty;
        string error = FormatDiagnostic(health.LastError);
        return $"{label} {health.Depth} now / {health.PeakDepth} peak · {health.Stage}{age}{error}";
    }

    public static string FormatCatalog(CatalogScanHealth health)
        => $"TAR catalog {health.Loaded:N0}/{health.FilesSeen:N0} loaded · " +
           $"expired {health.Expired:N0} · damaged {health.Damaged:N0} · " +
           $"inaccessible {health.Inaccessible:N0} · {FormatDuration(health.Duration)}";

    public static string FormatLatency(LatencyPercentiles latency)
        => $"RX end-to-end p50 {FormatDuration(latency.P50)} · " +
           $"p95 {FormatDuration(latency.P95)} · p99 {FormatDuration(latency.P99)}";

    public static string FormatRouteRecovery(RuntimeHealthSnapshot snapshot)
    {
        if (snapshot.RouteRecoveryAttempts == 0)
            return "Route recovery: none this session";
        string duration = snapshot.LastRouteRecoveryDuration is TimeSpan elapsed
            ? $" in {FormatDuration(elapsed)}"
            : string.Empty;
        string result = string.IsNullOrWhiteSpace(snapshot.LastRouteRecoveryResult)
            ? string.Empty
            : $" · {snapshot.LastRouteRecoveryResult}";
        return $"Route recovery: {snapshot.RouteRecoveryAttempts:N0} attempt(s){duration}{result}";
    }

    private static string FormatAge(TimeSpan? age)
        => age is TimeSpan value ? $" · sample age {FormatDuration(value)}" : string.Empty;

    private static string FormatDiagnostic(string? diagnostic)
        => string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : $" · {diagnostic.Trim()}";

    private static string FormatDuration(TimeSpan duration)
    {
        double milliseconds = Math.Max(0, duration.TotalMilliseconds);
        return milliseconds < 1
            ? "<1 ms"
            : milliseconds < 1_000
                ? $"{milliseconds.ToString("0", CultureInfo.InvariantCulture)} ms"
                : $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
    }
}
