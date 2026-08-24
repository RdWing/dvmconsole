using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;
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
        TaskObservation.Observe(ApplyAudioInputSettingsAsync(restartActiveAudio: false));
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
        bool highQualityBluetoothAudio = OperatingSystem.IsMacOSVersionAtLeast(26)
            ? HighQualityBluetoothAudioEnabled
            : userSettings.HighQualityBluetoothAudioEnabled;
        bool audioRouteChanged = previousProcessingMode != processingMode ||
            previousHighQualityBluetoothAudio != highQualityBluetoothAudio ||
            !previousInputDeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase) ||
            !previousOutputDeviceId.Equals(outputDeviceId, StringComparison.OrdinalIgnoreCase);
        if (restartActiveAudio && audioRouteChanged && transmitCoordinator.ActiveChannels.Count > 0)
        {
            AudioStatusText = "Stop transmitting before changing the audio processing mode or device route.";
            return;
        }

        userSettings.AudioInputDeviceId = deviceId;
        userSettings.AudioOutputDeviceId = outputDeviceId;
        userSettings.AudioProcessingMode = processingMode switch
        {
            AudioProcessingMode.AppleVoiceProcessing => UserSettings.AppleVoiceProcessingMode,
            AudioProcessingMode.WindowsCommunications => UserSettings.WindowsCommunicationsProcessingMode,
            _ => UserSettings.DvmConsoleAudioProcessingMode
        };
        userSettings.HighQualityBluetoothAudioEnabled = highQualityBluetoothAudio;
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
        bool restoreWarmMicrophone = userSettings.KeepTransmitMicrophoneWarm;
        if (restartActiveAudio && audioRouteChanged)
        {
            if (restoreWarmMicrophone)
                await transmitCoordinator.SetKeepMicrophoneWarmAsync(false).ConfigureAwait(false);
            await ReconfigureApplicationAudioAsync(CreateApplicationAudioConfiguration())
                .ConfigureAwait(false);
            if (restoreWarmMicrophone)
                await transmitCoordinator.SetKeepMicrophoneWarmAsync(true).ConfigureAwait(false);
        }
        else if (restoreWarmMicrophone)
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
        AudioStatusText = (processingMode switch
        {
            AudioProcessingMode.AppleVoiceProcessing =>
                "Apple voice processing saved; application playback and transmit capture now share one full-duplex voice route. RX vocoder processing remains independently controlled.",
            AudioProcessingMode.WindowsCommunications =>
                "Windows communications processing saved for microphone transmit capture; available effects depend on Windows and the selected endpoint.",
            _ => "DVM Console audio processing saved; device routes apply to the next audio session and PTT call."
        });

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
            audioSettings.SetResolvedDevices(
                ResolveAudioDeviceOption(audioInputDevices, AudioInputDeviceIdText),
                ResolveAudioDeviceOption(audioOutputDevices, AudioOutputDeviceIdText));
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
            audioSettings.SetResolvedDevices(input: null, output: null);
            RefreshAppleVoiceProcessingRouteState();
            AudioStatusText = $"Audio device list unavailable: {exception.Message}";
        }
    }

    private async Task HandleAudioDeviceTopologyChangedAsync(
        AudioDeviceTopologyChange change,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        await audioReconfigurationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReceiveRouteRecoveryResult outputRefresh = new([], [], null);
            if (change.OutputChanged)
            {
                outputRefresh = await audioCoordinator
                    .RefreshSystemDefaultOutputAsync(cancellationToken)
                    .ConfigureAwait(false);
                DateTimeOffset retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
                foreach (ChannelViewModel channel in outputRefresh.Restarted)
                {
                    receiveRetryAfter.Remove(channel);
                    receiveAudioWork.Start(channel);
                }
                foreach (ChannelViewModel channel in outputRefresh.Failed)
                    receiveRetryAfter[channel] = retryAt;
            }

            DefaultInputRefreshResult inputRefresh = change.InputChanged
                ? await transmitCoordinator
                    .RefreshSystemDefaultInputAsync(cancellationToken)
                    .ConfigureAwait(false)
                : DefaultInputRefreshResult.NotRequired;

            await RunOnUiThreadAsync(() =>
            {
                RefreshAudioDevices();
                AudioStatusText = DescribeAudioDeviceRefresh(outputRefresh, inputRefresh);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(() =>
                AudioStatusText = $"Audio devices changed, but a route could not be refreshed: {exception.Message}")
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            audioReconfigurationLock.Release();
        }
    }

    private static string DescribeAudioDeviceRefresh(
        ReceiveRouteRecoveryResult outputRefresh,
        DefaultInputRefreshResult inputRefresh)
    {
        if (outputRefresh.Failed.Count > 0)
        {
            return $"Audio defaults changed; restarted {outputRefresh.Restarted.Count} receive route(s), " +
                $"and {outputRefresh.Failed.Count} will be retried.";
        }
        if (inputRefresh == DefaultInputRefreshResult.DeferredUntilIdle)
        {
            return "Audio defaults changed; receive routes were updated and the microphone will switch after PTT ends.";
        }

        int refreshedRoutes = outputRefresh.Restarted.Count +
            (inputRefresh == DefaultInputRefreshResult.Refreshed ? 1 : 0);
        return refreshedRoutes > 0
            ? $"Audio defaults changed; refreshed {refreshedRoutes} active default route(s)."
            : "Audio devices changed; new sessions will use the current system defaults.";
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
            SelectedAudioProcessingMode == AppleVoiceProcessingDisplay)
        {
            SelectedAudioProcessingMode = DvmConsoleProcessingDisplay;
        }
    }

    private async Task ReconfigureApplicationAudioAsync(
        ApplicationAudioConfiguration configuration)
    {
        await audioReconfigurationLock.WaitAsync();
        try
        {
            ChannelViewModel[] activeChannels = audioCoordinator.ActiveChannels.ToArray();
            WebStreamViewModel[] activeStreams = webStreamPlayback.ActiveStreams.ToArray();

            foreach (ChannelViewModel channel in activeChannels)
                await receiveAudioWork.StopAsync(channel).ConfigureAwait(false);
            if (activeChannels.Length > 0)
                await audioCoordinator.StopAsync().ConfigureAwait(false);
            foreach (WebStreamViewModel stream in activeStreams)
                await webStreamPlayback.StopAsync(stream).ConfigureAwait(false);
            await webStreamPlayback.ResetAudioBackendAsync().ConfigureAwait(false);
            await recordingPlayback.ResetAudioBackendAsync().ConfigureAwait(false);
            await audioBackendProvider.ReconfigureAsync(configuration).ConfigureAwait(false);

            foreach (ChannelViewModel channel in activeChannels)
            {
                if (channel.IsAudioEnabled)
                    await StartAudioAsync(channel).ConfigureAwait(false);
                else if (channel.IsRecordingEnabled)
                    await EnsureRecordingAudioAsync(channel).ConfigureAwait(false);
            }
            foreach (WebStreamViewModel stream in activeStreams)
                await webStreamPlayback.StartAsync(stream).ConfigureAwait(false);

            int restarted = activeChannels.Count(audioCoordinator.IsActive);
            AudioStatusText =
                $"Audio route changed; restarted {restarted} of {activeChannels.Length} receive session(s) " +
                $"and {activeStreams.Count(webStreamPlayback.IsActive)} of {activeStreams.Length} web stream(s).";
        }
        finally
        {
            audioReconfigurationLock.Release();
        }
    }

    private async Task ApplyRxAudioProcessingOptionsAsync()
    {
        userSettings.RxAudioProcessingOptions = rxAudioProcessingModes.ToDictionary(
            mode => mode.SettingsKey,
            mode => mode.ToSetting(),
            StringComparer.OrdinalIgnoreCase);
        PersistUserSettings();
        foreach (RxAudioProcessingModeViewModel mode in rxAudioProcessingModes)
            mode.Restore(userSettings.RxAudioProcessingOptions[mode.SettingsKey]);
        Volatile.Write(ref receiveAudioProcessingOptions, BuildReceiveAudioProcessingOptions());

        try
        {
            await RestartReceiveVocoderSessionsAsync();
            AudioStatusText = "RX audio processing options saved and applied to receive sessions.";
        }
        catch (Exception exception)
        {
            AudioStatusText = $"RX audio processing options were saved, but active sessions could not restart: {exception.Message}";
        }
    }

    private IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>
        BuildReceiveAudioProcessingOptions()
        => rxAudioProcessingModes.ToDictionary(
            mode => mode.VocoderMode,
            mode => mode.ToVocoderOptions());

    internal async Task ApplyRxJitterBufferAsync(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!Systems.Contains(system))
            throw new ArgumentException("The FNE connection is not part of this console.", nameof(system));

        RxJitterBufferSetting configured = system.GetConfiguredJitterBuffer();
        userSettings.RxJitterBuffersBySystem[system.Name] = configured;
        PersistUserSettings();
        system.RestoreJitterBuffer(configured);
        Volatile.Write(
            ref receiveJitterBufferSettingsBySystem,
            BuildReceiveJitterBufferSettingsBySystem());
        adaptiveReceiveJitter.Reset(system.Name);
        receiveJitterEffectiveness.Reset(system.Name);
        RefreshJitterBufferTelemetry(system);

        try
        {
            await RestartReceiveVocoderSessionsAsync();
            AudioStatusText = $"{system.Name} RX jitter buffer settings saved and applied.";
        }
        catch (Exception exception)
        {
            AudioStatusText = $"{system.Name} RX jitter buffer settings were saved, but active sessions could not restart: {exception.Message}";
        }
    }

    private ReceiveJitterBufferProfile GetReceiveJitterBufferProfile(
        ChannelViewModel channel,
        FneTrafficProtocol protocol)
    {
        string systemName = channel.Definition.SystemName;
        RxJitterBufferSetting configured = GetReceiveJitterBufferSetting(systemName);
        ReceiveJitterBufferConfiguration configuration =
            ReceiveJitterBufferPolicy.GetConfiguration(protocol, configured);
        return adaptiveReceiveJitter.GetProfile(systemName, protocol, configuration);
    }

    private void ObserveAdaptiveReceiveJitter(
        SystemViewModel system,
        FneTrafficFrame traffic)
    {
        RxJitterBufferSetting configured = GetReceiveJitterBufferSetting(system.Name);
        ReceiveJitterBufferConfiguration configuration =
            ReceiveJitterBufferPolicy.GetConfiguration(traffic.Protocol, configured);
        adaptiveReceiveJitter.Observe(system.Name, traffic, configuration);
    }

    private RxJitterBufferSetting GetReceiveJitterBufferSetting(string systemName)
    {
        IReadOnlyDictionary<string, RxJitterBufferSetting> settings =
            Volatile.Read(ref receiveJitterBufferSettingsBySystem);
        return settings.TryGetValue(systemName, out RxJitterBufferSetting? systemSettings)
            ? systemSettings
            : RxJitterBufferSetting.Normalize(userSettings.RxJitterBuffer);
    }

    private IReadOnlyDictionary<string, RxJitterBufferSetting> BuildReceiveJitterBufferSettingsBySystem()
    {
        RxJitterBufferSetting fallback = RxJitterBufferSetting.Normalize(userSettings.RxJitterBuffer);
        var configured = new Dictionary<string, RxJitterBufferSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (SystemViewModel system in Systems)
        {
            RxJitterBufferSetting systemSettings = userSettings.RxJitterBuffersBySystem.TryGetValue(
                system.Name,
                out RxJitterBufferSetting? stored)
                    ? RxJitterBufferSetting.Normalize(stored)
                    : RxJitterBufferSetting.Normalize(fallback);
            system.RestoreJitterBuffer(systemSettings);
            configured[system.Name] = systemSettings;
        }
        return configured;
    }

    private void RefreshJitterBufferTelemetry(SystemViewModel system)
    {
        RxJitterBufferSetting settings = GetReceiveJitterBufferSetting(system.Name);
        ReceiveJitterBufferEffectiveness effectiveness =
            receiveJitterEffectiveness.GetSnapshot(system.Name);

        system.UpdateJitterBufferTelemetry(new ReceiveJitterBufferTelemetry(
            GetLearnedDelay(system.Name, FneTrafficProtocol.P25, settings),
            GetLearnedDelay(system.Name, FneTrafficProtocol.Dmr, settings),
            GetLearnedDelay(system.Name, FneTrafficProtocol.Nxdn, settings),
            settings.P25Adaptive,
            settings.DmrAdaptive,
            settings.NxdnAdaptive,
            effectiveness.RestoredDelayedPackets,
            effectiveness.DeadlineMissedPackets));
    }

    private TimeSpan GetLearnedDelay(
        string systemName,
        FneTrafficProtocol protocol,
        RxJitterBufferSetting settings)
    {
        ReceiveJitterBufferConfiguration configuration =
            ReceiveJitterBufferPolicy.GetConfiguration(protocol, settings);
        return adaptiveReceiveJitter.GetProfile(systemName, protocol, configuration).TargetDelay;
    }

    private async Task RestartReceiveVocoderSessionsAsync()
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        await audioReconfigurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ChannelViewModel[] activeChannels = audioCoordinator.ActiveChannels.ToArray();
            if (activeChannels.Length > 0)
            {
                foreach (ChannelViewModel channel in activeChannels)
                    await receiveAudioWork.StopAsync(channel).ConfigureAwait(false);
                await audioCoordinator.StopAsync().ConfigureAwait(false);
                foreach (ChannelViewModel channel in activeChannels)
                {
                    if (channel.IsAudioEnabled)
                        await StartAudioAsync(channel).ConfigureAwait(false);
                    else if (channel.IsRecordingEnabled)
                        await EnsureRecordingAudioAsync(channel).ConfigureAwait(false);
                }
            }

            ChannelViewModel[] patchChannels = GetActivePatchSourceChannels();
            await DrainPatchSourceWorkAsync().ConfigureAwait(false);
            await patchSourceDecode.StopAllAsync().ConfigureAwait(false);
            await patchSourceDecode.ApplyChannelsAsync(patchChannels).ConfigureAwait(false);
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
