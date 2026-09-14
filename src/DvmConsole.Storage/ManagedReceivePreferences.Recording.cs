// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Storage;

public sealed partial class ManagedReceivePreferences
{
    private sealed partial class ConfigurationScope : IConsoleRecordingSettingsStore
    {
        public ValueTask<RecordingRetentionPolicy> LoadRecordingRetentionAsync(CancellationToken cancellationToken = default)
            => owner.AccessAsync(settings => new RecordingRetentionPolicy(
                settings.RecordingRetentionDays, settings.RecordingRetentionPolicyAccepted), false, cancellationToken);

        public async ValueTask SaveRecordingRetentionAsync(RecordingRetentionPolicy policy, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(policy);
            policy.Validate();
            await owner.AccessAsync(settings =>
            {
                settings.RecordingRetentionDays = policy.Days;
                settings.RecordingRetentionPolicyAccepted = policy.Accepted;
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
        }
    }
}
