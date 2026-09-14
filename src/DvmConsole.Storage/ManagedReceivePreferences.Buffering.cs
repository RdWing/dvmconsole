// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Application;

namespace DvmConsole.Storage;

public sealed partial class ManagedReceivePreferences
{
    private sealed partial class ConfigurationScope : IConsoleReceiveBufferingStore
    {
        public ValueTask<ImmutableDictionary<string, ConsoleReceiveBufferingOptions>> LoadReceiveBufferingAsync(
            IReadOnlyList<string> systems, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(systems);
            var names = systems.ToArray();
            return owner.AccessAsync(settings => names.ToImmutableDictionary(name => name,
                name => ConsoleReceiveBufferingOptions.FromSetting(settings.RxJitterBuffersBySystem.GetValueOrDefault(name) ?? settings.RxJitterBuffer),
                StringComparer.OrdinalIgnoreCase), false, cancellationToken);
        }

        public async ValueTask SaveReceiveBufferingAsync(string system, ConsoleReceiveBufferingOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(system);
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            await owner.AccessAsync(settings =>
            {
                settings.RxJitterBuffersBySystem[system] = options.ToSetting();
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
        }
    }
}
