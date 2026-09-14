// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Presentation;

/// <summary>One timeline of session calls and recordings, attaching matching TAR files only once.</summary>
public sealed class UnifiedCallHistoryViewModel : HistoryProjectionViewModel<IHistoryCatalogFilterItem>
{
    private readonly ConsoleCallHistoryViewModel calls = new();
    private Dictionary<RecordingId, RecordingHistoryItemViewModel> archiveRows = [];

    private IReadOnlyList<RecordingArchiveEntry>? catalog;
    private ConsoleCallHistoryRecord[] previousCalls = [];

    public void Refresh(IReadOnlyList<ConsoleCallHistoryRecord> records,
        IReadOnlyList<RecordingArchiveEntry> recordings, Func<RecordingId, bool> isPlaying)
    {
        calls.Refresh(records, recordings, isPlaying);
        if (ReferenceEquals(catalog, recordings) && previousCalls.SequenceEqual(records))
        {
            foreach (var row in archiveRows.Values) row.Update(row.Recording, isPlaying(row.Recording.Id));
            return;
        }
        catalog = recordings;
        previousCalls = records.ToArray();
        var attached = new HashSet<RecordingId>();
        var next = new List<IHistoryCatalogFilterItem>(records.Count + recordings.Count);
        foreach (var call in calls.FilteredCallHistory)
        {
            next.Add(call);
            if (call.Recording is { } recording) attached.Add(recording.Id);
        }
        var retained = new Dictionary<RecordingId, RecordingHistoryItemViewModel>();
        foreach (var recording in recordings)
        {
            if (attached.Contains(recording.Id)) continue;
            if (!archiveRows.TryGetValue(recording.Id, out var row)) row = new(recording);
            row.Update(recording, isPlaying(recording.Id));
            retained.Add(recording.Id, row);
            next.Add(row);
        }
        archiveRows = retained;
        // Reuse row objects so playback and filter refreshes do not recreate controls.
        SetEntries(next.OrderByDescending(row => row.Timestamp).ToArray());
    }
}
