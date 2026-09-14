// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleRecordingSettings
{
    private RecordingRetentionPolicy recordingRetention = new();
    public RecordingRetentionPolicy RecordingRetention => Volatile.Read(ref recordingRetention);
    public bool CanSaveRecordingRetention => dependencies.Preferences is IConsoleRecordingSettingsStore &&
        dependencies.Recordings is IRecordingRetentionControl;

    public async Task<RecordingRetentionPreview> PreviewRecordingRetentionAsync(int days, CancellationToken cancellationToken = default)
    {
        new RecordingRetentionPolicy(days).Validate();
        RecordingRetentionPreview result = null!;
        await RunCommandAsync(async token =>
        {
            var control = dependencies.Recordings as IRecordingRetentionControl
                ?? throw new NotSupportedException("Recording retention is unavailable.");
            result = await control.PreviewRetentionAsync(days, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask SetRecordingRetentionAsync(RecordingRetentionPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        return RunCommandAsync(async token =>
        {
            var store = dependencies.Preferences as IConsoleRecordingSettingsStore
                ?? throw new NotSupportedException("Recording preferences are unavailable.");
            var control = dependencies.Recordings as IRecordingRetentionControl
                ?? throw new NotSupportedException("Recording retention is unavailable.");
            await store.SaveRecordingRetentionAsync(policy, token).ConfigureAwait(false);
            control.RetentionDays = policy.Days;
            Volatile.Write(ref recordingRetention, policy);
            try
            {
                int removed = await PruneRecordingRetentionAsync(token).ConfigureAwait(false);
                SetStatus(!policy.Accepted || policy.Days == 0
                    ? "Automatic recording deletion is disabled."
                    : $"Recording retention saved: {policy.Days} days. Removed {removed} expired recording(s).");
            }
            catch (Exception exception)
            {
                SetStatus($"Recording retention saved, but cleanup failed: {exception.Message}");
                throw;
            }
        }, cancellationToken);
    }

    private async Task RestoreRecordingRetentionAsync(CancellationToken token)
    {
        if (dependencies.Preferences is not IConsoleRecordingSettingsStore store ||
            dependencies.Recordings is not IRecordingRetentionControl control) return;
        var policy = await store.LoadRecordingRetentionAsync(token).ConfigureAwait(false);
        policy.Validate();
        control.RetentionDays = policy.Days;
        Volatile.Write(ref recordingRetention, policy);
    }

    private Task<int> PruneRecordingRetentionAsync(CancellationToken token)
        => RecordingRetention is { Accepted: true, Days: > 0 } &&
            dependencies.Recordings is IRecordingRetentionControl control
                ? control.PruneExpiredAsync(token) : Task.FromResult(0);
}
