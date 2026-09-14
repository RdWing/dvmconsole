// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Storage;

public sealed partial class ManagedReceivePreferences
{
    private sealed partial class ConfigurationScope
    {
        public ValueTask<ConsoleManualTransmitOptions> LoadManualTransmitOptionsAsync(CancellationToken cancellationToken = default)
            => owner.AccessAsync(settings => new ConsoleManualTransmitOptions(
                settings.TalkPermitTone, settings.MuteRxAudioWhileTransmitting), false, cancellationToken);

        public async ValueTask SaveManualTransmitOptionsAsync(ConsoleManualTransmitOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            await owner.AccessAsync(settings =>
            {
                settings.TalkPermitTone = options.TalkPermitTone;
                settings.MuteRxAudioWhileTransmitting = options.MuteReceiveWhileTransmitting;
                return true;
            }, true, cancellationToken).ConfigureAwait(false);
        }
    }
}
