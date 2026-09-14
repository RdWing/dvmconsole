// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Media;

namespace DvmConsole.Application;

public interface IConsoleDmrReceiveKeySettings
{
    bool RequireConfiguredDmrReceiveKey { get; }
    bool CanSaveDmrReceiveKeyPolicy { get; }
    ValueTask SetRequireConfiguredDmrReceiveKeyAsync(bool required, CancellationToken cancellationToken = default);
}

public interface IConsoleDmrReceiveKeyPreferences
{
    ValueTask<bool> LoadRequireConfiguredDmrReceiveKeyAsync(CancellationToken cancellationToken = default);
    ValueTask SaveRequireConfiguredDmrReceiveKeyAsync(bool required, CancellationToken cancellationToken = default);
}

public sealed partial class ConsoleReceiveSession : IConsoleDmrReceiveKeySettings
{
    private bool requireConfiguredDmrReceiveKey;
    public bool RequireConfiguredDmrReceiveKey => Volatile.Read(ref requireConfiguredDmrReceiveKey);
    public bool CanSaveDmrReceiveKeyPolicy => !IsStopping && dependencies.Preferences is IConsoleDmrReceiveKeyPreferences;

    private DmrReceiveKeyPolicy GetDmrReceiveKeyPolicy() => RequireConfiguredDmrReceiveKey
        ? DmrReceiveKeyPolicy.ConfiguredChannel : DmrReceiveKeyPolicy.OnAirMetadata;

    private async ValueTask RestoreDmrReceiveKeyPolicyAsync(CancellationToken token)
    {
        if (dependencies.Preferences is IConsoleDmrReceiveKeyPreferences preferences)
            Volatile.Write(ref requireConfiguredDmrReceiveKey,
                await preferences.LoadRequireConfiguredDmrReceiveKeyAsync(token).ConfigureAwait(false));
    }

    public ValueTask SetRequireConfiguredDmrReceiveKeyAsync(bool required, CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            var preferences = dependencies.Preferences as IConsoleDmrReceiveKeyPreferences
                ?? throw new NotSupportedException("DMR receive key settings are unavailable.");
            await preferences.SaveRequireConfiguredDmrReceiveKeyAsync(required, token).ConfigureAwait(false);
            Volatile.Write(ref requireConfiguredDmrReceiveKey, required);
            try
            {
                // Patch replacement closes ingress and drops interrupted calls before
                // creating new decoders. Local RX and TAR retain their selection intent.
                await RefreshLivePatchesAsync(token, rebuildDecoders: true).ConfigureAwait(false);
                await receive.RebuildDecodersAsync(cancellationToken: token,
                    canRestoreListening: () => state.Execution.Snapshot.CanReceive).ConfigureAwait(false);
                SetStatus(required
                    ? "DMR receive now requires the channel's configured algorithm and key ID."
                    : "DMR receive now follows on-air key identifiers within each FNE system.");
            }
            catch (Exception exception)
            {
                SetStatus($"DMR key policy saved, but decoder replacement failed. Apply again to retry: {exception.Message}");
                throw;
            }
        }, cancellationToken);
}
