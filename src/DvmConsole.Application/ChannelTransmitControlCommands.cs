// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal enum TransmitSelectionKind { Transmit, Page, Alert }
internal readonly record struct ChannelSelectionResult(
    ChannelId Channel, TransmitSelectionKind Kind, bool Selected, bool Applied);

/// <summary>Applies transmit selection and encryption after the preference adapter accepts the change.</summary>
internal sealed class ChannelTransmitControlCommands(
    ConsoleTransmitChannelDirectory channels,
    Func<ChannelId, bool> canSelect,
    Func<ChannelId, ChannelTransmitPreferenceChange, CancellationToken, ValueTask> save,
    Func<bool>? isStopping = null, Action<ChannelSelectionResult>? selectionChanged = null)
{
    public async ValueTask<bool> SetSelectedAsync(ChannelId id, bool selected, CancellationToken token = default)
    {
        CheckAdmission(token);
        if (!canSelect(id))
        {
            selectionChanged?.Invoke(new(id, TransmitSelectionKind.Transmit, selected, false));
            return false;
        }
        await save(id, new(Selected: selected), token).ConfigureAwait(false);
        CheckAdmission(token);
        channels.State(id).Operator.SetTransmitSelected(selected);
        selectionChanged?.Invoke(new(id, TransmitSelectionKind.Transmit, selected, true));
        return true;
    }

    /// <summary>Prepares every preference before committing a bulk selection as one state update.</summary>
    public async ValueTask<(int Count, bool Selected)> SetSelectionAsync(
        IEnumerable<ChannelId> scope, bool? selected,
        Func<IReadOnlyList<ChannelId>, bool, CancellationToken, ValueTask>? saveBatch = null,
        Action<Action>? applyState = null, CancellationToken token = default)
    {
        CheckAdmission(token);
        ChannelId[] candidates = scope.Distinct().Where(canSelect).ToArray();
        bool value = selected ?? candidates.Any(id => !channels.State(id).Operator.Snapshot.TransmitSelected);
        ChannelId[] changes = candidates.Where(id => channels.State(id).Operator.Snapshot.TransmitSelected != value).ToArray();
        if (changes.Length > 0)
        {
            if (saveBatch is not null)
                await saveBatch(changes, value, token).ConfigureAwait(false);
            else
                foreach (ChannelId id in changes)
                    await save(id, new(Selected: value), token).ConfigureAwait(false);
        }
        void Commit()
        {
            CheckAdmission(token);
            foreach (ChannelId id in changes) channels.State(id).Operator.SetTransmitSelected(value);
        }
        if (applyState is null) Commit();
        else applyState(Commit);
        return (candidates.Length, value);
    }

    public bool SetToneSelected(ChannelId id, bool selected, ConsoleToneTargets target, CancellationToken token = default)
    {
        CheckAdmission(token);
        if (!Enum.IsDefined(target)) throw new ArgumentOutOfRangeException(nameof(target));
        TransmitSelectionKind kind = target == ConsoleToneTargets.Page ? TransmitSelectionKind.Page : TransmitSelectionKind.Alert;
        if (!canSelect(id))
        {
            selectionChanged?.Invoke(new(id, kind, selected, false));
            return false;
        }
        var state = channels.State(id).Operator;
        if (target == ConsoleToneTargets.Page) state.SetPageSelected(selected);
        else state.SetAlertSelected(selected);
        selectionChanged?.Invoke(new(id, kind, selected, true));
        return true;
    }

    private void CheckAdmission(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(isStopping?.Invoke() == true, this);
    }

    public async ValueTask<bool> SetEncryptedAsync(ChannelId id, bool encrypted, CancellationToken token = default)
    {
        CheckAdmission(token);
        if (!channels.CanChangeEncryption(id, encrypted)) return false;
        await save(id, new(Encrypted: encrypted), token).ConfigureAwait(false);
        CheckAdmission(token);
        channels.State(id).Operator.SetTransmitEncrypted(encrypted);
        return true;
    }
}
