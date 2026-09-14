// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Storage;

public sealed partial class ManagedReceivePreferences
{
    private sealed partial class ConfigurationScope : IConsoleConnectionCuePreferences
    {
        public ValueTask<bool> LoadConnectionChimesAsync(CancellationToken cancellationToken = default)
            => owner.AccessAsync(settings => settings.ConnectionChimes, false, cancellationToken);

        public async ValueTask SaveConnectionChimesAsync(bool enabled, CancellationToken cancellationToken = default)
            => await owner.AccessAsync(settings =>
            {
                settings.ConnectionChimes = enabled;
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
    }
}
