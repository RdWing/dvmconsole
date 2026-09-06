// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

internal static class RecordingPlaybackText
{
    public static string Describe(string channelName, DateTimeOffset timestamp)
        => $"{channelName} · {timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
}
