// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public interface IConsoleRecordingPlaybackCommands
{
    bool IsRecordingPlaying(RecordingId id);
    ValueTask PlayRecordingAsync(RecordingId id, CancellationToken cancellationToken = default);
    ValueTask StopRecordingPlaybackAsync(CancellationToken cancellationToken = default);
}
