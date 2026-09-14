// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Application;

namespace DvmConsole.Storage;

/// <summary>Catalog actions over the existing leased TAR store, never caller-provided paths.</summary>
public sealed class OpusRecordingArchive(OpusRecordingStore store) : IConsoleRecordingArchive
{
    public long Revision => store.CatalogRevision;

    public async Task<IReadOnlyList<RecordingArchiveEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var recordings = await store.LoadRecordingsAsync(cancellationToken).ConfigureAwait(false);
        return recordings.Where(item => Guid.TryParse(item.RecordingId, out _))
            .OrderByDescending(item => item.UtcStartTime).Select(Describe).ToImmutableArray();
    }

    private static RecordingArchiveEntry Describe(CallRecordingMetadata item)
    {
        ImmutableArray<uint> streams = item.StreamIds.Count > 0
            ? item.StreamIds.ToImmutableArray()
            : item.StreamId is { } stream ? [stream] : [];
        return new(new RecordingId(Guid.Parse(item.RecordingId)), item.UtcStartTime,
            TimeSpan.FromMilliseconds(Math.Max(0, item.DurationMs)), item.SystemName, item.ChannelName,
            item.Direction, item.Protocol, item.SubscriberText, item.TalkgroupText, item.SubscriberAlias,
            item.RouteText, streams, item.EffectiveEncryptionState, item.EncryptionText,
            item.IsPlayable, item.FileName, item.DetailText)
        {
            CallIdentity = item.ToCallIdentity()
        };
    }

    public Task ExportAsync(RecordingId id, Stream destination, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await using var source = await store.OpenReadAsync(id, cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<bool> DeleteAsync(RecordingId id, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var metadata = store.LoadRecordings(cancellationToken)
                .FirstOrDefault(item => Guid.TryParse(item.RecordingId, out var value) && value == id.Value);
            cancellationToken.ThrowIfCancellationRequested();
            return metadata is not null && store.DeleteRecording(metadata);
        }, cancellationToken);
}
