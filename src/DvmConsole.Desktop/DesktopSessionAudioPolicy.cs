// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using DvmConsole.Vocoder;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

/// <summary>Translates persisted desktop choices into host audio policy without a shell.</summary>
internal sealed class DesktopSessionAudioPolicy(UserSettings settings)
{
    public static AudioProcessingMode ResolveProcessingMode(string? configured, bool isWindows)
        => isWindows && configured == UserSettings.WindowsCommunicationsProcessingMode
            ? AudioProcessingMode.WindowsCommunications : AudioProcessingMode.DvmConsole;

    public ApplicationAudioConfiguration BackendConfiguration => new(
        ResolveProcessingMode(settings.AudioProcessingMode, OperatingSystem.IsWindows()),
        settings.AudioInputDeviceId, settings.AudioOutputDeviceId);

    public AudioInputProcessingOptions Input => new()
    {
        DeviceId = settings.AudioInputDeviceId,
        ProcessingMode = BackendConfiguration.ProcessingMode,
        AgcEnabled = settings.AudioInputAgcEnabled,
        AgcTargetDbfs = settings.AudioInputAgcTargetDbfs,
        Gain = settings.AudioInputGain,
        LowGainDb = settings.AudioInputEqLowGainDb,
        MidGainDb = settings.AudioInputEqMidGainDb,
        HighGainDb = settings.AudioInputEqHighGainDb
    };

    public string? OutputDevice(string channelSettingsKey)
        => settings.ChannelOutputDeviceIds.TryGetValue(channelSettingsKey, out string? output)
            ? output : settings.AudioOutputDeviceId;

    public DmrReceiveKeyPolicy ReceiveKeyPolicy => settings.RequireConfiguredDmrReceiveKey
        ? DmrReceiveKeyPolicy.ConfiguredChannel : DmrReceiveKeyPolicy.OnAirMetadata;

    public string RecordingRoot(string settingsPath)
        => string.IsNullOrWhiteSpace(settings.RecordingRootPath)
            ? Path.Combine(Path.GetDirectoryName(settingsPath) ?? AppContext.BaseDirectory, "Recordings")
            : Path.GetFullPath(settings.RecordingRootPath.Trim());

    public IReadOnlyDictionary<string, ConsoleReceiveBufferingOptions> ReceiveBuffering(IEnumerable<string> systems)
    {
        var fallback = RxJitterBufferSetting.Normalize(settings.RxJitterBuffer);
        return systems.ToDictionary(name => name, name => ConsoleReceiveBufferingOptions.FromSetting(
            settings.RxJitterBuffersBySystem.TryGetValue(name, out var value)
                ? RxJitterBufferSetting.Normalize(value) : fallback), StringComparer.OrdinalIgnoreCase);
    }

    public static IVocoderBackend CreatePatchVocoder(IVocoderFactory factory)
        // Patches retain unprocessed PCM even when decoder defaults enable filters.
        => factory.Create(Enum.GetValues<VocoderMode>().ToDictionary(mode => mode,
            _ => new ReceiveAudioProcessingOptions
            { HighPassFilterEnabled = false, PeakingFilterEnabled = false, CompressorEnabled = false }));
}
