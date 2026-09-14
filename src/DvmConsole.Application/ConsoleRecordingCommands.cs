// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Channel recording selection, separate from manual transmit commands.</summary>
public interface IConsoleRecordingCommands
{
    bool CanRecord(ChannelId channel) => true;
    ValueTask SetRecordingEnabledAsync(ChannelId channel, bool enabled, CancellationToken cancellationToken = default);
}
