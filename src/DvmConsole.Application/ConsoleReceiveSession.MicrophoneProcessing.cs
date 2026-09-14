// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleMicrophoneProcessingSettings
{
    private AudioInputProcessingOptions microphoneProcessing = new();
    public AudioInputProcessingOptions MicrophoneProcessing => Volatile.Read(ref microphoneProcessing);
    public bool CanSaveMicrophoneProcessing => dependencies.ManualInput is not null &&
        dependencies.Preferences is IConsoleMicrophoneProcessingStore;

    // These settings never choose a device or platform processing mode.
    private AudioInputProcessingOptions NormalizeMicrophoneProcessing(AudioInputProcessingOptions options)
        => new AudioInputProcessingOptions
        {
            DeviceId = dependencies.ManualInput?.DeviceId ?? "default",
            ProcessingMode = dependencies.ManualInput?.ProcessingMode ?? AudioProcessingMode.DvmConsole,
            Gain = options.Gain,
            LowGainDb = options.LowGainDb,
            MidGainDb = options.MidGainDb,
            HighGainDb = options.HighGainDb,
            AgcEnabled = options.AgcEnabled,
            AgcTargetDbfs = options.AgcTargetDbfs
        }.Normalize();

    public ValueTask SetMicrophoneProcessingAsync(AudioInputProcessingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        AudioInputProcessingOptions captured = NormalizeMicrophoneProcessing(options);
        return RunCommandAsync(async token =>
        {
            if (!CanSaveMicrophoneProcessing)
                throw new NotSupportedException("Microphone processing settings are unavailable.");
            var store = (IConsoleMicrophoneProcessingStore)dependencies.Preferences!;
            await store.SaveMicrophoneProcessingAsync(captured, token).ConfigureAwait(false);
            Volatile.Write(ref microphoneProcessing, captured);
            SetStatus("Microphone processing saved for the next manual transmission.");
        }, cancellationToken);
    }
}
