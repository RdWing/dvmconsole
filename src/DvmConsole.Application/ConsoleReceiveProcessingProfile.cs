// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Core.Settings;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

public sealed record ConsoleReceiveProcessingMode(string SettingsKey, string Name, VocoderMode Mode);

public static class ConsoleReceiveProcessingProfile
{
    public static ImmutableArray<ConsoleReceiveProcessingMode> Modes { get; } =
    [
        new(RxAudioProcessingModeSetting.P25Phase1Mode, "P25 Phase 1", VocoderMode.P25Imbe),
        new(RxAudioProcessingModeSetting.P25Phase2Mode, "P25 Phase 2", VocoderMode.P25Phase2Ambe),
        new(RxAudioProcessingModeSetting.DmrMode, "DMR", VocoderMode.DmrAmbe),
        new(RxAudioProcessingModeSetting.NxdnMode, "NXDN", VocoderMode.NxdnAmbe)
    ];

    public static ImmutableDictionary<VocoderMode, ReceiveAudioProcessingOptions> Defaults { get; }
        = Modes.ToImmutableDictionary(mode => mode.Mode, _ => new ReceiveAudioProcessingOptions());

    // Patch PCM is a transport source, independent of the operator's RX enhancements.
    public static ImmutableDictionary<VocoderMode, ReceiveAudioProcessingOptions> PatchSource { get; }
        = Enum.GetValues<VocoderMode>().ToImmutableDictionary(mode => mode, _ => new ReceiveAudioProcessingOptions
        {
            HighPassFilterEnabled = false,
            PeakingFilterEnabled = false,
            CompressorEnabled = false
        });

    public static ImmutableDictionary<VocoderMode, ReceiveAudioProcessingOptions> Capture(
        IReadOnlyDictionary<string, RxAudioProcessingModeSetting> settings)
        => Modes.ToImmutableDictionary(mode => mode.Mode, mode =>
            FromSetting((settings.GetValueOrDefault(mode.SettingsKey) ?? new RxAudioProcessingModeSetting()).Normalize()));

    public static ReceiveAudioProcessingOptions Normalize(ReceiveAudioProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return FromSetting(ToSetting(options).Normalize());
    }

    public static ReceiveAudioProcessingOptions FromSetting(RxAudioProcessingModeSetting setting) => new()
    {
        HighPassFilterEnabled = setting.HighPassFilterEnabled,
        HighPassFrequencyHz = (float)setting.HighPassFrequencyHz,
        PeakingFilterEnabled = setting.PeakingFilterEnabled,
        PeakingFrequencyHz = (float)setting.PeakingFrequencyHz,
        PeakingGainDb = (float)setting.PeakingGainDb,
        CompressorEnabled = setting.CompressorEnabled,
        CompressorRatio = (float)setting.CompressorRatio,
        CompressorThresholdDbfs = (float)setting.CompressorThresholdDbfs,
        CompressorMakeupGainDb = (float)setting.CompressorMakeupGainDb
    };

    public static RxAudioProcessingModeSetting ToSetting(ReceiveAudioProcessingOptions options) => new()
    {
        HighPassFilterEnabled = options.HighPassFilterEnabled,
        HighPassFrequencyHz = options.HighPassFrequencyHz,
        PeakingFilterEnabled = options.PeakingFilterEnabled,
        PeakingFrequencyHz = options.PeakingFrequencyHz,
        PeakingGainDb = options.PeakingGainDb,
        CompressorEnabled = options.CompressorEnabled,
        CompressorRatio = options.CompressorRatio,
        CompressorThresholdDbfs = options.CompressorThresholdDbfs,
        CompressorMakeupGainDb = options.CompressorMakeupGainDb
    };
}

public interface IConsoleReceiveProcessingStore
{
    ValueTask<ImmutableDictionary<VocoderMode, ReceiveAudioProcessingOptions>> LoadReceiveProcessingAsync(CancellationToken cancellationToken = default);
    ValueTask SaveReceiveProcessingAsync(VocoderMode mode, ReceiveAudioProcessingOptions options, CancellationToken cancellationToken = default);
}
