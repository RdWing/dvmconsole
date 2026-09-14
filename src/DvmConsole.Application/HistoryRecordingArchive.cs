// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Reconciles catalog facts with session History independently of host navigation.</summary>
internal sealed class HistoryRecordingArchive(IConsoleRecordingArchive archive, ConsoleCallHistory history)
    : IConsoleRecordingArchive
{
    public long Revision => archive.Revision;

    public async Task<IReadOnlyList<RecordingArchiveEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        long revision = archive.Revision;
        var recordings = await archive.LoadAsync(cancellationToken).ConfigureAwait(false);
        history.ReconcileRecordings(recordings, () => archive.Revision == revision);
        return recordings;
    }

    public Task ExportAsync(RecordingId id, Stream destination, CancellationToken cancellationToken = default)
        => archive.ExportAsync(id, destination, cancellationToken);

    public async Task<bool> DeleteAsync(RecordingId id, CancellationToken cancellationToken = default)
    {
        if (!await archive.DeleteAsync(id, cancellationToken).ConfigureAwait(false)) return false;
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
