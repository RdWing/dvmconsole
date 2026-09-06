// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

// Presentation matching must never open files or resolve physical identities.
// Recording IDs are authoritative; lexical paths support legacy catalog rows.
// File authorization, deletion, and containment retain their physical checks.
internal static class RecordingDisplayIdentity
{
    public static bool Matches(CallRecordingMetadata? candidate, RecordingId id, string path)
        => candidate is not null && (RecordingIdentity.Parse(candidate) is RecordingId candidateId
            ? candidateId == id
            : PathsMatch(candidate.FilePath, path));

    public static bool Matches(CallRecordingMetadata? left, CallRecordingMetadata right)
    {
        if (left is null)
            return false;
        if (RecordingIdentity.Parse(left) is RecordingId leftId &&
            RecordingIdentity.Parse(right) is RecordingId rightId)
            return leftId == rightId;
        return PathsMatch(left.FilePath, right.FilePath);
    }

    private static bool PathsMatch(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
