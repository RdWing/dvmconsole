// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

internal enum UiLatencyCategory
{
    ReceiveActivity,
    RecordingPlay,
    RecordingStop
}

internal readonly record struct UiLatencyObservation(
    UiLatencyCategory Category, TimeSpan QueueDelay, TimeSpan ApplyDuration, string? Context = null);

// Fixed storage and monotonic throttling keep diagnostics cheap during long
// sessions. Observations measure UI application, not rendering or audibility.
internal sealed class UiLatencyReporter(
    Action<UiLatencyObservation> publish,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);
    private readonly Action<UiLatencyObservation> sink = publish ?? throw new ArgumentNullException(nameof(publish));
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly long?[] lastPublished = new long?[Enum.GetValues<UiLatencyCategory>().Length];
    private readonly object sync = new();

    public void Observe(UiLatencyCategory category, TimeSpan queueDelay, TimeSpan applyDuration, string? context = null)
    {
        if (queueDelay < Threshold && applyDuration < Threshold)
            return;
        lock (sync)
        {
            long now = clock.GetTimestamp();
            int index = (int)category;
            if (lastPublished[index] is long last && clock.GetElapsedTime(last, now) < MinimumInterval)
                return;
            lastPublished[index] = now;
        }
        try
        {
            sink(new(category, queueDelay, applyDuration, context));
        }
        catch
        {
            // Diagnostics must not interrupt receive or playback presentation.
        }
    }
}
