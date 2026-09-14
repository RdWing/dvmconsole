// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed record RecordingRetentionPolicy(int Days = 7, bool Accepted = false)
{
    public void Validate()
    {
        if (Days is < 0 or > 3650) throw new ArgumentOutOfRangeException(nameof(Days));
    }
}

public sealed record RecordingRetentionPreview(DateTimeOffset? Cutoff, int CandidateCount);

public interface IConsoleRecordingSettings
{
    RecordingRetentionPolicy RecordingRetention { get; }
    bool CanSaveRecordingRetention { get; }
    Task<RecordingRetentionPreview> PreviewRecordingRetentionAsync(int days, CancellationToken cancellationToken = default);
    ValueTask SetRecordingRetentionAsync(RecordingRetentionPolicy policy, CancellationToken cancellationToken = default);
}

public interface IConsoleRecordingSettingsStore
{
    ValueTask<RecordingRetentionPolicy> LoadRecordingRetentionAsync(CancellationToken cancellationToken = default);
    ValueTask SaveRecordingRetentionAsync(RecordingRetentionPolicy policy, CancellationToken cancellationToken = default);
}

/// <summary>Recording policy over the existing capture and leased catalog owner.</summary>
public interface IRecordingRetentionControl
{
    int RetentionDays { get; set; }
    Task<RecordingRetentionPreview> PreviewRetentionAsync(int days, CancellationToken cancellationToken = default);
    Task<int> PruneExpiredAsync(CancellationToken cancellationToken = default);
}
