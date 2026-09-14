// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;

namespace DvmConsole.Storage;

public sealed partial class ManagedReceivePreferences
{
    private sealed partial class ConfigurationScope
    {
        public ValueTask<AudioInputProcessingOptions> LoadMicrophoneProcessingAsync(CancellationToken cancellationToken = default)
            => owner.AccessAsync(settings => new AudioInputProcessingOptions
            {
                Gain = settings.AudioInputGain,
                LowGainDb = settings.AudioInputEqLowGainDb,
                MidGainDb = settings.AudioInputEqMidGainDb,
                HighGainDb = settings.AudioInputEqHighGainDb,
                AgcEnabled = settings.AudioInputAgcEnabled,
                AgcTargetDbfs = settings.AudioInputAgcTargetDbfs
            }.Normalize(), false, cancellationToken);

        public async ValueTask SaveMicrophoneProcessingAsync(AudioInputProcessingOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            AudioInputProcessingOptions captured = options.Normalize();
            await owner.AccessAsync(settings =>
            {
                settings.AudioInputGain = captured.Gain;
                settings.AudioInputEqLowGainDb = captured.LowGainDb;
                settings.AudioInputEqMidGainDb = captured.MidGainDb;
                settings.AudioInputEqHighGainDb = captured.HighGainDb;
                settings.AudioInputAgcEnabled = captured.AgcEnabled;
                settings.AudioInputAgcTargetDbfs = captured.AgcTargetDbfs;
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
        }
    }
}
