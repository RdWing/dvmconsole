// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Presentation;
using DvmConsole.Vocoder;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IAudioInputSettingsSession
{
    public bool HasUndoNotification => operatorUndo.CanUndo;
    public string UndoNotificationText => operatorUndo.Message;

    public async Task UndoLastOperatorActionAsync()
        => _ = await operatorUndo.UndoAsync().ConfigureAwait(false);

    void IAudioInputSettingsSession.BeginUndoableAction(
        string message,
        Func<ValueTask> undo,
        Func<ValueTask>? commit)
        => BeginUndoableAction(message, undo, commit);

    private void BeginUndoableAction(
        string message,
        Func<ValueTask> undo,
        Func<ValueTask>? commit = null)
        => operatorUndo.Begin(message, undo, commit);

    private void HandleOperatorUndoChanged(object? sender, EventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUndoNotification)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UndoNotificationText)));
    }

    public void SaveAudioInputPreset() => audioInputSettingsController.SavePreset();

    public void UseAudioInputPreset(AudioInputPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        audioInputSettingsController.LoadPreset(preset);
        TaskObservation.Observe(
            ApplyAudioInputSettingsAsync(restartActiveAudio: false),
            HandleAudioCommandFault);
    }

    public void DeleteAudioInputPreset(AudioInputPresetViewModel preset)
    {
        audioInputSettingsController.DeletePreset(preset);
    }

    private Task ApplyAudioInputSettingsAsync(bool restartActiveAudio)
        => audioInputSettingsController.ApplyAsync(restartActiveAudio);

    private static AudioInputProcessingOptions CreateAudioInputProcessingOptions(
        string deviceId,
        AudioProcessingMode processingMode,
        bool agcEnabled,
        double agcTargetDbfs,
        double gain,
        double lowGainDb,
        double midGainDb,
        double highGainDb)
        => new()
        {
            DeviceId = deviceId,
            ProcessingMode = processingMode,
            AgcEnabled = agcEnabled,
            AgcTargetDbfs = agcTargetDbfs,
            Gain = gain,
            LowGainDb = lowGainDb,
            MidGainDb = midGainDb,
            HighGainDb = highGainDb
        };

    private void HandleAudioCommandFault(Exception exception)
    {
        DesktopCrashLog.Write("Audio settings command", exception);
        TaskObservation.Observe(
            RunOnUiThreadAsync(() =>
            {
                AudioStatusText = $"Unable to apply audio settings: {exception.Message}";
                AddDebugLog(
                    DateTimeOffset.Now,
                    "AUDIO",
                    DebugLogSeverity.Warning,
                    $"Audio settings command failed: {exception}");
            }),
            reportingException => DesktopCrashLog.Write(
                "Audio settings command UI reporting",
                reportingException));
    }

    public void RefreshAudioDevices()
    {
        if (networkDisabledDemo)
        {
            InstallDemoAudioDevices();
            return;
        }

        try
        {
            using IAudioBackend backend = audioBackendFactory.Create(AudioBackendConfiguration.Default);
            IReadOnlyList<AudioDeviceInfo> inputs = backend.EnumerateDevices(AudioDirection.Input);
            IReadOnlyList<AudioDeviceInfo> outputs = backend.EnumerateDevices(AudioDirection.Output);

            ReplaceAudioDeviceOptions(audioInputDevices, inputs);
            ReplaceAudioDeviceOptions(audioOutputDevices, outputs);
            foreach (WebStreamViewModel stream in WebStreams)
                stream.RefreshOutputDeviceSelection();
            foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
                channel.RefreshOutputDeviceSelection();
            audioSettings.SetResolvedDevices(
                ResolveAudioDeviceOption(audioInputDevices, AudioInputDeviceIdText),
                ResolveAudioDeviceOption(audioOutputDevices, AudioOutputDeviceIdText));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or DllNotFoundException or PlatformNotSupportedException)
        {
            audioInputDevices.Clear();
            audioOutputDevices.Clear();
            foreach (WebStreamViewModel stream in WebStreams)
                stream.RefreshOutputDeviceSelection();
            foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
                channel.RefreshOutputDeviceSelection();
            audioSettings.SetResolvedDevices(input: null, output: null);
            AudioStatusText = $"Audio device list unavailable: {exception.Message}";
        }
    }

    private void InstallDemoAudioDevices()
    {
        ReplaceAudioDeviceOptions(
            audioInputDevices,
            [new AudioDeviceInfo(
                "neo-demo-input",
                "NEO Demo Microphone (synthetic)",
                AudioDirection.Input,
                IsDefault: true,
                IsBluetooth: false)]);
        ReplaceAudioDeviceOptions(
            audioOutputDevices,
            [new AudioDeviceInfo(
                "neo-demo-output",
                "NEO Demo Output (synthetic)",
                AudioDirection.Output,
                IsDefault: true,
                IsBluetooth: false)]);
        audioSettings.AudioInputDeviceIdText = "neo-demo-input";
        audioSettings.AudioOutputDeviceIdText = "neo-demo-output";
        audioSettings.SetResolvedDevices(
            ResolveAudioDeviceOption(audioInputDevices, "neo-demo-input"),
            ResolveAudioDeviceOption(audioOutputDevices, "neo-demo-output"));
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
        {
            channel.SetOutputDeviceOptions(audioOutputDevices);
            channel.RestoreOutputDeviceId("neo-demo-output");
        }
        AudioStatusText = "Demo audio uses synthetic endpoints; no device was opened.";
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
                foreach (ChannelViewModel channel in ResolveChannels(outputRefresh.Restarted))
                {
                    receiveSessions.RecordRestarted(channel);
                    receiveAudioWork.Start(channel);
                }
                foreach (ChannelViewModel channel in ResolveChannels(outputRefresh.Failed))
                    receiveSessions.RecordFailure(channel, retryAt);
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

    private async Task ReconfigureApplicationAudioAsync(
        ApplicationAudioConfiguration configuration)
    {
        await audioReconfigurationLock.WaitAsync();
        try
        {
            ChannelViewModel[] activeChannels = ResolveChannels(audioCoordinator.ActiveChannels);
            WebStreamViewModel[] activeStreams = webStreamOperator.ActiveStreams.ToArray();

            foreach (ChannelViewModel channel in activeChannels)
                await receiveAudioWork.StopAsync(channel).ConfigureAwait(false);
            if (activeChannels.Length > 0)
                await audioCoordinator.StopAsync().ConfigureAwait(false);
            foreach (WebStreamViewModel stream in activeStreams)
                await webStreamOperator.StopPlaybackAsync(stream).ConfigureAwait(false);
            await webStreamOperator.ResetAudioBackendAsync().ConfigureAwait(false);
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
                await webStreamOperator.StartPlaybackAsync(stream).ConfigureAwait(false);

            int restarted = activeChannels.Count(channel => audioCoordinator.IsActive(channel));
            AudioStatusText =
                $"Audio route changed; restarted {restarted} of {activeChannels.Length} receive session(s) " +
                $"and {activeStreams.Count(webStreamOperator.IsActive)} of {activeStreams.Length} web stream(s).";
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
            await RestartReceiveVocoderSessionsAsync(includePatchSources: false);
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
        return adaptiveReceiveJitter.GetProfile(
            systemName,
            FneReceiveWorkQueueAdapter.ToRadioProtocol(protocol),
            configuration);
    }

    private void ObserveAdaptiveReceiveJitter(
        SystemViewModel system,
        FneTrafficFrame traffic)
    {
        RxJitterBufferSetting configured = GetReceiveJitterBufferSetting(system.Name);
        ReceiveJitterBufferConfiguration configuration =
            ReceiveJitterBufferPolicy.GetConfiguration(traffic.Protocol, configured);
        adaptiveReceiveJitter.Observe(
            system.Name,
            traffic,
            traffic.TransportIngressTimestamp,
            configuration);
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
        return adaptiveReceiveJitter.GetProfile(
            systemName,
            FneReceiveWorkQueueAdapter.ToRadioProtocol(protocol),
            configuration).TargetDelay;
    }

    private async Task RestartReceiveVocoderSessionsAsync(bool includePatchSources = true)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        await audioReconfigurationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ChannelViewModel[] activeChannels = ResolveChannels(audioCoordinator.ActiveChannels);
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

            if (includePatchSources)
            {
                ChannelViewModel[] patchChannels = GetActivePatchSourceChannels();
                await DrainPatchSourceWorkAsync().ConfigureAwait(false);
                await patchSourceDecode.StopAllAsync().ConfigureAwait(false);
                await patchSourceDecode.ApplyChannelsAsync(
                    patchChannels.Select(channel => (DvmConsole.Application.ReceiveChannelDescriptor)channel))
                    .ConfigureAwait(false);
            }
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
        AudioDeviceInfo? systemDefault = devices.FirstOrDefault(device => device.IsDefault);
        target.Add(new AudioDeviceOptionViewModel(
            "default",
            "System default",
            true,
            systemDefault?.IsBluetooth));
        foreach (AudioDeviceInfo device in devices)
        {
            if (device.Id.Equals("default", StringComparison.OrdinalIgnoreCase))
                continue;
            target.Add(new AudioDeviceOptionViewModel(
                device.Id,
                device.Name,
                device.IsDefault,
                device.IsBluetooth));
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

    bool IAudioInputSettingsSession.HasActiveTransmission
        => transmitCoordinator.ActiveChannels.Count > 0;

    bool IAudioInputSettingsSession.KeepTransmitMicrophoneWarm
        => userSettings.KeepTransmitMicrophoneWarm;

    ApplicationAudioConfiguration IAudioInputSettingsSession.CurrentConfiguration
        => CreateApplicationAudioConfiguration();

    AudioProcessingMode IAudioInputSettingsSession.SelectedProcessingMode
        => GetSelectedAudioProcessingMode();

    void IAudioInputSettingsSession.UpdateTransmitInputOptions(AudioInputProcessingOptions options)
        => transmitCoordinator.UpdateAudioInputOptions(options);

    Task IAudioInputSettingsSession.ApplyRuntimeSettingsAsync(
        bool reconfigureRoute,
        ApplicationAudioConfiguration previousConfiguration,
        ApplicationAudioConfiguration proposedConfiguration,
        bool restoreWarmMicrophone)
        => audioRuntimeSettings.ApplyAsync(
            reconfigureRoute,
            previousConfiguration,
            proposedConfiguration,
            restoreWarmMicrophone);

    void IAudioInputSettingsSession.PersistUserSettings() => PersistUserSettings();

    void IAudioInputSettingsSession.SetAudioStatus(string status) => AudioStatusText = status;

}
