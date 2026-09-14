// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Commits TAR intent after storage accepts it, then reconciles recording audio.</summary>
internal sealed class ChannelRecordingCommands(
    Func<ChannelId, ConsoleChannelState> channel,
    Func<ChannelId, bool> canRecord,
    Func<ChannelId, bool, CancellationToken, ValueTask> save,
    Action<Action> applyState,
    Action<ChannelId> changed,
    Func<ChannelId, bool, CancellationToken, Task> reconcile,
    Func<bool>? isStopping = null)
{
    private void CheckAdmission(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(isStopping?.Invoke() == true, this);
    }

    public bool CanRecord(ChannelId id) => canRecord(id);

    public async ValueTask<bool> SetEnabledAsync(ChannelId id, bool enabled, CancellationToken token = default)
    {
        CheckAdmission(token);
        if (enabled && !CanRecord(id)) return false;
        await save(id, enabled, token).ConfigureAwait(false);
        CheckAdmission(token);
        applyState(() =>
        {
            CheckAdmission(token);
            channel(id).Operator.SetRecordingEnabled(enabled);
            changed(id);
        });
        CheckAdmission(token);
        await reconcile(id, enabled, token).ConfigureAwait(false);
        return true;
    }
}
