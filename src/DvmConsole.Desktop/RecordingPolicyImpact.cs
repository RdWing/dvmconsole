// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

internal sealed record RecordingPolicyImpact(
    string RootPath,
    int RetentionDays,
    DateTimeOffset? Cutoff,
    int CandidateCount)
{
    public string SummaryText
        => RetentionDays == 0
            ? $"Folder: {RootPath}\nRetention: disabled\nRecordings eligible for deletion: 0"
            : $"Folder: {RootPath}\nCutoff: {Cutoff:yyyy-MM-dd HH:mm:ss 'UTC'}\n" +
              $"Recordings eligible for deletion: {CandidateCount:N0}";
}
