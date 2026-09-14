// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Application;
using DvmConsole.Vocoder;

namespace DvmConsole.Storage;

public sealed partial class ManagedReceivePreferences
{
    private sealed partial class ConfigurationScope : IConsoleDmrReceiveKeyPreferences
    {
        public ValueTask<bool> LoadRequireConfiguredDmrReceiveKeyAsync(CancellationToken cancellationToken = default)
            => owner.AccessAsync(settings => settings.RequireConfiguredDmrReceiveKey, false, cancellationToken);

        public async ValueTask SaveRequireConfiguredDmrReceiveKeyAsync(bool required, CancellationToken cancellationToken = default)
            => await owner.AccessAsync(settings =>
            {
                settings.RequireConfiguredDmrReceiveKey = required;
                return true;
            }, true, cancellationToken).ConfigureAwait(false);

        public ValueTask<ImmutableDictionary<VocoderMode, ReceiveAudioProcessingOptions>> LoadReceiveProcessingAsync(CancellationToken cancellationToken = default)
            => owner.AccessAsync(settings => ConsoleReceiveProcessingProfile.Capture(settings.RxAudioProcessingOptions),
                false, cancellationToken);

        public async ValueTask SaveReceiveProcessingAsync(VocoderMode mode, ReceiveAudioProcessingOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            options = ConsoleReceiveProcessingProfile.Normalize(options);
            var profile = ConsoleReceiveProcessingProfile.Modes.FirstOrDefault(profile => profile.Mode == mode)
                ?? throw new ArgumentOutOfRangeException(nameof(mode));
            await owner.AccessAsync(settings =>
            {
                settings.RxAudioProcessingOptions[profile.SettingsKey] = ConsoleReceiveProcessingProfile.ToSetting(options);
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
        }
    }
}
