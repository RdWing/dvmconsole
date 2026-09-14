// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>
/// Saves channel mix intent before applying it to live audio. The session owns
/// command ordering and admission; adapters supply persistence and presentation.
/// </summary>
internal sealed class ChannelAudioSettingsController(
    Func<ChannelId, ChannelOperatorState> resolve,
    Func<ChannelId, ChannelReceivePreferenceChange, CancellationToken, ValueTask> save,
    Func<ChannelId, double, CancellationToken, Task> applyGain,
    Func<ChannelId, double, CancellationToken, Task> applyBalance,
    Func<bool> isStopping,
    Action<ChannelId>? changed = null)
{
    public async Task SetGainAsync(ChannelId id, double value, CancellationToken token = default)
    {
        CheckAdmission(token);
        ChannelOperatorState state = resolve(id);
        double gain = ChannelOperatorState.NormalizeGain(value);
        await save(id, new(Gain: gain), token).ConfigureAwait(false);
        CheckAdmission(token);
        state.SetGain(gain);
        changed?.Invoke(id);
        await applyGain(id, gain, token).ConfigureAwait(false);
    }

    public async Task SetBalanceAsync(ChannelId id, double value, CancellationToken token = default)
    {
        CheckAdmission(token);
        ChannelOperatorState state = resolve(id);
        double balance = ChannelOperatorState.NormalizeBalance(value);
        await save(id, new(Balance: balance), token).ConfigureAwait(false);
        CheckAdmission(token);
        state.SetBalance(balance);
        changed?.Invoke(id);
        await applyBalance(id, balance, token).ConfigureAwait(false);
    }

    private void CheckAdmission(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(isStopping(), this);
    }
}
