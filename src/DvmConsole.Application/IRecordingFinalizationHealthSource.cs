// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public interface IRecordingFinalizationHealthSource
{
    RecordingFinalizationSpoolHealth FinalizationHealth { get; }
}

public sealed record RecordingFinalizationSpoolHealth(
    int PendingJobs,
    int QuarantinedJobs,
    TimeSpan? OldestAge,
    string? LastError);
