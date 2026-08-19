using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    public void SaveAudioInputPreset()
    {
        if (!TryParseBounded(AudioInputGainText, 0.25, 3.0, out double gain) ||
            !TryParseBounded(AudioInputLowGainText, -12, 12, out double lowGainDb) ||
            !TryParseBounded(AudioInputMidGainText, -12, 12, out double midGainDb) ||
            !TryParseBounded(AudioInputHighGainText, -12, 12, out double highGainDb))
        {
            AudioStatusText = "Microphone presets require gain 0.25–3.0 and EQ values from -12 to 12 dB.";
            return;
        }

        string name = string.IsNullOrWhiteSpace(AudioInputPresetNameText)
            ? $"Mic preset {audioInputPresets.Count + 1}"
            : AudioInputPresetNameText.Trim();
        if (name.Length > 80)
        {
            AudioStatusText = "Microphone preset names must be 80 characters or fewer.";
            return;
        }

        AudioInputPresetViewModel next = new(new AudioInputPresetSetting
        {
            Name = name,
            Gain = gain,
            LowGainDb = lowGainDb,
            MidGainDb = midGainDb,
            HighGainDb = highGainDb
        });
        int existingIndex = audioInputPresets
            .Select((preset, index) => (preset, index))
            .Where(item => item.preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (existingIndex >= 0 && existingIndex < audioInputPresets.Count)
            audioInputPresets[existingIndex] = next;
        else
            audioInputPresets.Add(next);

        AudioInputPresetNameText = name;
        PersistAudioInputPresetState();
        AudioStatusText = $"Microphone preset '{name}' saved.";
    }

    public void UseAudioInputPreset(AudioInputPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        AudioInputPresetNameText = preset.Name;
        AudioInputGainText = preset.Gain.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputLowGainText = preset.LowGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputMidGainText = preset.MidGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputHighGainText = preset.HighGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        _ = ApplyAudioInputSettingsAsync(restartActiveAudio: false);
        AudioStatusText = $"Microphone preset '{preset.Name}' loaded.";
    }

    public void DeleteAudioInputPreset(AudioInputPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!audioInputPresets.Remove(preset))
            return;

        if (AudioInputPresetNameText.Equals(preset.Name, StringComparison.OrdinalIgnoreCase))
            AudioInputPresetNameText = string.Empty;
        PersistAudioInputPresetState();
        AudioStatusText = $"Microphone preset '{preset.Name}' deleted.";
    }

    private async Task ApplyAudioInputSettingsAsync(bool restartActiveAudio)
    {
        if (string.IsNullOrWhiteSpace(AudioInputDeviceIdText) || AudioInputDeviceIdText.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(AudioOutputDeviceIdText) || AudioOutputDeviceIdText.Trim().Length > 256 ||
            !TryParseBounded(AudioInputGainText, 0.25, 3.0, out double gain) ||
            !TryParseBounded(AudioInputAgcTargetDbfsText, -40, -12, out double agcTargetDbfs) ||
            !TryParseBounded(AudioInputLowGainText, -12, 12, out double lowGainDb) ||
            !TryParseBounded(AudioInputMidGainText, -12, 12, out double midGainDb) ||
            !TryParseBounded(AudioInputHighGainText, -12, 12, out double highGainDb))
        {
            AudioStatusText = "Microphone settings require a device ID, gain 0.25–3.0, AGC target -40 to -12 dBFS, and EQ values from -12 to 12 dB.";
            return;
        }

        string previousInputDeviceId = userSettings.AudioInputDeviceId;
        string previousOutputDeviceId = userSettings.AudioOutputDeviceId;
        AudioProcessingMode previousProcessingMode = GetConfiguredAudioProcessingMode();
        bool previousHighQualityBluetoothAudio = userSettings.HighQualityBluetoothAudioEnabled;
        AudioProcessingMode processingMode = GetSelectedAudioProcessingMode();
        string deviceId = AudioInputDeviceIdText.Trim();
        string outputDeviceId = AudioOutputDeviceIdText.Trim();
        userSettings.AudioInputDeviceId = deviceId;
        userSettings.AudioOutputDeviceId = outputDeviceId;
        userSettings.AudioProcessingMode = processingMode == AudioProcessingMode.AppleVoiceProcessing
            ? UserSettings.AppleVoiceProcessingMode
            : UserSettings.DvmConsoleAudioProcessingMode;
        if (OperatingSystem.IsMacOSVersionAtLeast(26))
            userSettings.HighQualityBluetoothAudioEnabled = HighQualityBluetoothAudioEnabled;
        userSettings.AudioInputAgcEnabled = AudioInputAgcEnabled;
        userSettings.AudioInputAgcTargetDbfs = agcTargetDbfs;
        userSettings.AudioInputGain = gain;
        userSettings.AudioInputEqLowGainDb = lowGainDb;
        userSettings.AudioInputEqMidGainDb = midGainDb;
        userSettings.AudioInputEqHighGainDb = highGainDb;
        PersistAudioInputPresetState();
        transmitCoordinator.UpdateAudioInputOptions(new AudioInputProcessingOptions
        {
            DeviceId = deviceId,
            ProcessingMode = processingMode,
            AgcEnabled = AudioInputAgcEnabled,
            AgcTargetDbfs = agcTargetDbfs,
            Gain = gain,
            LowGainDb = lowGainDb,
            MidGainDb = midGainDb,
            HighGainDb = highGainDb
        });
        if (userSettings.KeepTransmitMicrophoneWarm)
        {
            await transmitCoordinator.SetKeepMicrophoneWarmAsync(false).ConfigureAwait(false);
            await transmitCoordinator.SetKeepMicrophoneWarmAsync(true).ConfigureAwait(false);
        }
        PersistUserSettings();
        AudioInputDeviceIdText = deviceId;
        AudioOutputDeviceIdText = outputDeviceId;
        AudioInputGainText = gain.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputAgcTargetDbfsText = agcTargetDbfs.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputLowGainText = lowGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputMidGainText = midGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        AudioInputHighGainText = highGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        string bluetoothStatus = userSettings.HighQualityBluetoothAudioEnabled
            ? " High-quality Bluetooth audio is enabled for compatible AirPods; unsupported routes fall back safely."
            : string.Empty;
        AudioStatusText = (processingMode == AudioProcessingMode.AppleVoiceProcessing
            ? "Apple voice processing saved for microphone transmit capture; receive audio remains unprocessed."
            : "DVM Console audio processing saved; device routes apply to the next audio session and PTT call.") +
            bluetoothStatus;

        bool audioRouteChanged = previousProcessingMode != processingMode ||
            previousHighQualityBluetoothAudio != userSettings.HighQualityBluetoothAudioEnabled ||
            !previousInputDeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase) ||
            !previousOutputDeviceId.Equals(outputDeviceId, StringComparison.OrdinalIgnoreCase);
        if (restartActiveAudio && audioRouteChanged)
            await RestartActiveListeningChannelsAsync();
    }

    public void RefreshAudioDevices()
    {
        try
        {
            using IAudioBackend backend = AudioBackendFactory.CreateDefault(
                Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY"));
            IReadOnlyList<AudioDeviceInfo> inputs = backend.EnumerateDevices(AudioDirection.Input);
            IReadOnlyList<AudioDeviceInfo> outputs = backend.EnumerateDevices(AudioDirection.Output);

            ReplaceAudioDeviceOptions(audioInputDevices, inputs);
            ReplaceAudioDeviceOptions(audioOutputDevices, outputs);
            foreach (WebStreamViewModel stream in webStreams)
                stream.RefreshOutputDeviceSelection();
            foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
                channel.RefreshOutputDeviceSelection();
            selectedAudioInputDevice = ResolveAudioDeviceOption(audioInputDevices, AudioInputDeviceIdText);
            selectedAudioOutputDevice = ResolveAudioDeviceOption(audioOutputDevices, AudioOutputDeviceIdText);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioInputDevice)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioOutputDevice)));
            RefreshAppleVoiceProcessingRouteState();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or DllNotFoundException or PlatformNotSupportedException)
        {
            audioInputDevices.Clear();
            audioOutputDevices.Clear();
            foreach (WebStreamViewModel stream in webStreams)
                stream.RefreshOutputDeviceSelection();
            foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
                channel.RefreshOutputDeviceSelection();
            selectedAudioInputDevice = null;
            selectedAudioOutputDevice = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioInputDevice)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedAudioOutputDevice)));
            RefreshAppleVoiceProcessingRouteState();
            AudioStatusText = $"Audio device list unavailable: {exception.Message}";
        }
    }

    internal static bool IsAppleVoiceProcessingDevicePairCompatible(
        AudioDeviceOptionViewModel? input,
        AudioDeviceOptionViewModel? output)
    {
        if (input is null || output is null)
            return false;
        return input.Id.Equals(output.Id, StringComparison.OrdinalIgnoreCase) ||
            (input.IsDefault && output.IsDefault);
    }

    private void RefreshAppleVoiceProcessingRouteState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioProcessingModeOptions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAppleVoiceProcessingRouteCompatible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppleVoiceProcessingRouteDescription)));
        if (!IsAppleVoiceProcessingRouteCompatible &&
            selectedAudioProcessingMode == AppleVoiceProcessingDisplay)
        {
            SelectedAudioProcessingMode = DvmConsoleProcessingDisplay;
        }
    }

    private async Task RestartActiveListeningChannelsAsync()
    {
        await audioReconfigurationLock.WaitAsync();
        try
        {
            ChannelViewModel[] activeChannels = audioCoordinator.ActiveChannels.ToArray();
            if (activeChannels.Length == 0)
                return;

            await audioCoordinator.StopAsync();
            foreach (ChannelViewModel channel in activeChannels)
                await StartAudioAsync(channel);

            int restarted = activeChannels.Count(audioCoordinator.IsActive);
            AudioStatusText = restarted == activeChannels.Length
                ? $"Audio settings changed; restarted {restarted} active listening channel(s)."
                : $"Audio settings changed; restarted {restarted} of {activeChannels.Length} listening channel(s).";
        }
        finally
        {
            audioReconfigurationLock.Release();
        }
    }

    private static void ReplaceAudioDeviceOptions(
        ObservableCollection<AudioDeviceOptionViewModel> target,
        IReadOnlyList<AudioDeviceInfo> devices)
    {
        target.Clear();
        target.Add(new AudioDeviceOptionViewModel("default", "System default", true));
        foreach (AudioDeviceInfo device in devices)
        {
            if (device.Id.Equals("default", StringComparison.OrdinalIgnoreCase))
                continue;
            target.Add(new AudioDeviceOptionViewModel(device.Id, device.Name, device.IsDefault));
        }
    }

    private static AudioDeviceOptionViewModel? ResolveAudioDeviceOption(
        IEnumerable<AudioDeviceOptionViewModel> devices,
        string? requestedId)
    {
        return devices.FirstOrDefault(device => !string.IsNullOrWhiteSpace(requestedId) &&
                                                 device.Id.Equals(requestedId, StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault(device => device.IsDefault)
               ?? devices.FirstOrDefault();
    }

    private void PersistAudioInputPresetState()
    {
        userSettings.AudioInputPresetName = AudioInputPresetNameText.Trim();
        userSettings.AudioInputPresets = audioInputPresets
            .Select(preset => preset.ToSetting())
            .ToList();
        PersistUserSettings();
    }

    private static bool TryParseBounded(string value, double minimum, double maximum, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            double.IsFinite(result) && result >= minimum && result <= maximum;
    }

}
