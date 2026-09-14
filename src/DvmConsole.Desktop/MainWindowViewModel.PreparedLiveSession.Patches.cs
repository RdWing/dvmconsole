// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    internal sealed partial class PreparedLiveSession
    {
        public PatchConfigurationRuntime PatchConfiguration => Runtime.PatchConfiguration
            ?? throw new InvalidOperationException("Patch configuration is not initialized.");

        public async Task SynchronizePatchSourcesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await Runtime.Patches.Decoder.ApplyChannelsAsync(
                    PatchSourceSelectionPolicy.SelectEnabledSources(PatchConfiguration.SavedGroups)
                        .Select(Runtime.Media.DescribeReceive), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Status.SetAudio($"Patch source decode unavailable: {exception.Message}");
            }
        }
    }
}
