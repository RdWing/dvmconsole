using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Groups restoration and event-wiring phases that configure an already
// composed console session. Keeping these phases explicit makes the shell
// constructor read as an ordered composition workflow.
public sealed partial class MainWindowViewModel
{
    private void RestoreToolbarClocks()
    {
        List<ToolbarClockSetting> configuredClocks = (userSettings.ToolbarClocks ?? [])
            .Take(UserSettings.MaximumToolbarClocks)
            .ToList();
        while (configuredClocks.Count < UserSettings.MaximumToolbarClocks)
            configuredClocks.Add(new ToolbarClockSetting());
        for (int index = 0; index < configuredClocks.Count; index++)
            toolbarClocks.Add(new ToolbarClockViewModel(index + 1, configuredClocks[index]));
        RefreshClock();
    }

    private void RestoreChannelPresentation()
    {
        RestoreChannelWidgetLayout();
        foreach (ZoneViewModel zone in Zones)
        {
            zone.SetWidgetCardHeight(ChannelCardHeight);
            zone.SetDarkMode(userSettings.DarkMode);
        }
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels).Distinct())
            channel.SetDarkMode(userSettings.DarkMode);
    }

    private void ConfigureWebStreams()
    {
        foreach (WebStreamViewModel stream in Zones.SelectMany(zone => zone.WebStreams))
        {
            stream.SetOutputDeviceOptions(AudioOutputDevices);
            stream.SetInitialVolume(
                userSettings.WebStreamVolumes.TryGetValue(stream.Name, out double savedVolume)
                    ? savedVolume
                    : 1.0);
            stream.RestoreOutputDeviceId(
                userSettings.WebStreamOutputDeviceIds.TryGetValue(stream.Name, out string? savedOutputDeviceId)
                    ? savedOutputDeviceId
                    : string.Empty);
            stream.VolumeChanged += HandleWebStreamVolumeChanged;
            stream.PropertyChanged += HandleWebStreamPropertyChanged;
            stream.Configure(StartWebStreamAsync, StopWebStreamAsync);
            webStreams.Add(stream);
        }
    }

    private void ConfigureChannels()
    {
        foreach (ChannelViewModel channel in Systems.SelectMany(system => system.Channels))
        {
            channel.SetOutputDeviceOptions(AudioOutputDevices);
            if (channel.Definition.SelectableEncryption &&
                userSettings.TransmitEncryptionStates.TryGetValue(channel.SettingsKey, out bool savedEncryptionState))
            {
                channel.RestoreTransmitEncryption(savedEncryptionState);
            }

            channel.RestoreVolume(
                userSettings.ChannelVolumes.TryGetValue(channel.SettingsKey, out double savedVolume)
                    ? savedVolume
                    : 1.0);
            channel.RestoreStereoBalance(
                userSettings.ChannelStereoBalances.TryGetValue(channel.SettingsKey, out double savedBalance)
                    ? savedBalance
                    : 0.0);
            channel.RestoreOutputDeviceId(
                userSettings.ChannelOutputDeviceIds.TryGetValue(channel.SettingsKey, out string? savedOutputDeviceId)
                    ? savedOutputDeviceId
                    : string.Empty);
            channel.RestoreRecordingEnabled(userSettings.RecordingEnabledChannelKeys.Contains(
                channel.SettingsKey,
                StringComparer.OrdinalIgnoreCase));
            channel.TransmitEncryptionChanged += HandleChannelEncryptionChanged;
            channel.RecordingStateChanged += HandleChannelRecordingChanged;
            channel.VolumeChanged += HandleChannelVolumeChanged;
            channel.StereoBalanceChanged += HandleChannelStereoBalanceChanged;
            channel.PropertyChanged += HandleActivityChannelPropertyChanged;
            channel.SetIgnoredSubscriberIds(
                userSettings.RecordingIgnoredSubscriberIds.TryGetValue(
                    channel.SettingsKey,
                    out List<uint>? ignoredSubscriberIds)
                    ? ignoredSubscriberIds
                    : []);
            channel.ConfigureAudio(
                candidate => ChangeChannelReceiveSelectionAsync(candidate, enabled: true),
                candidate => ChangeChannelReceiveSelectionAsync(candidate, enabled: false));
            channel.ConfigureTransmit(StartTransmitAsync, StopTransmitAsync);
            if (userSettings.RestoreSelectedChannelsOnStartup &&
                userSettings.ReceiveEnabledChannelKeys.Contains(
                    channel.SettingsKey,
                    StringComparer.OrdinalIgnoreCase))
            {
                channel.SetAudioEnabled(true);
            }
            channel.RestoreTransmitSelection(userSettings.TransmitSelectedChannelKeys.Contains(
                channel.SettingsKey,
                StringComparer.OrdinalIgnoreCase));
            if (channel.IsRecordingEnabled)
                TaskObservation.Observe(EnsureRecordingAudioAsync(channel));
        }
    }

    private void SubscribeToSystems()
    {
        foreach (SystemViewModel system in Systems)
        {
            system.JitterBufferChanged += HandleSystemJitterBufferChanged;
            system.PropertyChanged += HandleSystemPropertyChanged;
            system.StatusChanged += HandleSubscribedSystemStatus;
            system.LogReceived += HandleSystemLog;
            system.TrafficReceived += HandleSubscribedSystemTraffic;
            system.KeyResponseReceived += HandleSystemKeyResponse;
            system.TalkgroupAuthorityChanged += HandleSystemTalkgroupAuthorityChanged;
        }
    }

    private void HandleSystemTalkgroupAuthorityChanged(
        object? sender,
        FneTalkgroupAuthority authority)
    {
        if (sender is not SystemViewModel system)
            return;

        void Apply()
        {
            if (Volatile.Read(ref disposeStarted) != 0)
                return;

            IReadOnlyList<ChannelViewModel> newlyUnavailable =
                system.ApplyTalkgroupAuthority(authority);
            if (newlyUnavailable.Count == 0)
                return;

            int stoppedPatchTargets = patchForwarding.StopUnavailableTargets(newlyUnavailable);
            ChannelViewModel[] activeChannels = transmitCoordinator.ActiveChannels.ToArray();
            bool stopConsoleTransmission = activeChannels.Any(newlyUnavailable.Contains);
            string channels = string.Join(", ", newlyUnavailable.Select(DescribeUnavailableTalkgroup));
            string stopped = stopConsoleTransmission || stoppedPatchTargets > 0
                ? " Active transmission stopped."
                : string.Empty;
            string message =
                $"{system.Name}: FNE talkgroup table does not allow {channels}; PTT disabled.{stopped}";
            StatusText = message;
            TransmitStatusText = message;
            AddDebugLog(DateTimeOffset.Now, "TX", DebugLogSeverity.Warning, message);
            if (stopConsoleTransmission)
            {
                TaskObservation.Observe(
                    StopTransmitForTalkgroupAuthorityAsync(activeChannels, message));
            }
        }

        if (uiDispatcher.CheckAccess())
            Apply();
        else
            uiDispatcher.Post(Apply);
    }

    private async Task StopTransmitForTalkgroupAuthorityAsync(
        IReadOnlyCollection<ChannelViewModel> activeChannels,
        string message)
    {
        await StopTransmitAsync(activeChannels).ConfigureAwait(false);
        await RunOnUiThreadAsync(() => TransmitStatusText = message).ConfigureAwait(false);
    }

    private static string DescribeUnavailableTalkgroup(ChannelViewModel channel)
        => channel.Definition.Protocol == ChannelProtocol.Dmr
            ? $"{channel.Name} (TG {channel.Definition.DestinationId}, TS{channel.Definition.Slot + 1})"
            : $"{channel.Name} (TG {channel.Definition.DestinationId}, {channel.ModeText})";

    private void HandleSubscribedSystemStatus(object? sender, FneConnectionStatus status)
    {
        if (sender is SystemViewModel system)
            HandleSystemStatus(system, status);
    }

    private void HandleSubscribedSystemTraffic(object? sender, FneTrafficFrame traffic)
    {
        if (sender is SystemViewModel system)
            HandleSystemTraffic(system, traffic);
    }

    private void RestoreInitialSelection()
    {
        selectedChannel = userSettings.RestoreSelectedChannelsOnStartup
            ? Systems
                .SelectMany(system => system.Channels)
                .FirstOrDefault(channel => channel.SettingsKey.Equals(
                    userSettings.LastSelectedChannelKey,
                    StringComparison.Ordinal))
            : null;
        selectedSystem = userSettings.RestoreSelectedChannelsOnStartup
            ? Systems.FirstOrDefault(system => system.Name.Equals(
                userSettings.LastSelectedSystemName,
                StringComparison.OrdinalIgnoreCase)) ??
                Systems.FirstOrDefault(system => selectedChannel is not null && system.Channels.Contains(selectedChannel)) ??
                (Systems.Count > 0 ? Systems[0] : null)
            : Systems.Count > 0 ? Systems[0] : null;
        foreach (SystemViewModel system in Systems)
            system.SetSelected(ReferenceEquals(system, selectedSystem));
    }
}
