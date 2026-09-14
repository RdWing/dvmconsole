// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

/// <summary>Path-free catalog facts for completed and recoverable TAR media.</summary>
public sealed record RecordingArchiveEntry(RecordingId Id, DateTimeOffset StartedAt, TimeSpan Duration,
    string SystemName, string ChannelName, string Direction, string Protocol, string Subscriber,
    string Talkgroup, string Alias, string Route, ImmutableArray<uint> StreamIds,
    CallRecordingEncryptionState EncryptionState, string EncryptionText, bool IsPlayable,
    string FileName, string Details)
{
    public RecordingCallIdentity? CallIdentity { get; init; }
}

public interface IConsoleRecordingArchive
{
    // Changes caused through the app-owned store; external locations are not live backing files.
    long Revision => 0;
    Task<IReadOnlyList<RecordingArchiveEntry>> LoadAsync(CancellationToken cancellationToken = default);
    Task ExportAsync(RecordingId id, Stream destination, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(RecordingId id, CancellationToken cancellationToken = default);
}
