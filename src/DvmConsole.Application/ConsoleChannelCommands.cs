// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Stable-ID channel entry points for the operational graph, independent of host views.</summary>
internal sealed class ConsoleChannelCommands(ConsoleOperationalRuntime runtime, Func<bool> isStopping)
    : IConsoleCommands, IConsoleRecordingCommands
{
    public bool CanRecord(ChannelId channelId) => runtime.RecordingControls.CanRecord(channelId);

    public async ValueTask SetRecordingEnabledAsync(ChannelId channelId, bool enabled, CancellationToken cancellationToken = default)
        => await runtime.RecordingControls.SetEnabledAsync(channelId, enabled, cancellationToken).ConfigureAwait(false);

    public ValueTask SetReceiveEnabledAsync(ChannelId channelId, bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(isStopping(), this);
        return runtime.Receive.Output.SetEnabledAsync(channelId, enabled, cancellationToken);
    }

    public async ValueTask<bool> BeginPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        => await runtime.Transmit.BeginChannelAsync(channelId, cancellationToken: cancellationToken).ConfigureAwait(false);

    public ValueTask EndPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
        // Release is cleanup. A cancelled caller must still be able to unkey.
        => new(runtime.Transmit.EndChannelAsync(channelId));

    public async ValueTask SetTransmitSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
        => await runtime.TransmitControls.SetSelectedAsync(channelId, selected, cancellationToken).ConfigureAwait(false);

    public ValueTask SetPageSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
        => SetToneSelection(channelId, selected, ConsoleToneTargets.Page, cancellationToken);

    public ValueTask SetAlertSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
        => SetToneSelection(channelId, selected, ConsoleToneTargets.Alert, cancellationToken);

    public async ValueTask SetTransmitEncryptedAsync(ChannelId channelId, bool encrypted, CancellationToken cancellationToken = default)
        => await runtime.TransmitControls.SetEncryptedAsync(channelId, encrypted, cancellationToken).ConfigureAwait(false);

    public ValueTask SetChannelGainAsync(ChannelId channelId, double gain, CancellationToken cancellationToken = default)
        => new(runtime.AudioSettings.SetGainAsync(channelId, gain, cancellationToken));

    public ValueTask SetChannelBalanceAsync(ChannelId channelId, double balance, CancellationToken cancellationToken = default)
        => new(runtime.AudioSettings.SetBalanceAsync(channelId, balance, cancellationToken));

    private ValueTask SetToneSelection(ChannelId channelId, bool selected, ConsoleToneTargets target, CancellationToken token)
    {
        runtime.TransmitControls.SetToneSelected(channelId, selected, target, token);
        return ValueTask.CompletedTask;
    }
}
