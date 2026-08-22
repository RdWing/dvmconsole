namespace DvmConsole.Core.Settings;

internal sealed class UserSettingsNormalizationPipeline
{
    public UserSettings NormalizeAfterLoad(UserSettings settings)
    {
        int storedSchemaVersion = settings.SchemaVersion;
        if (storedSchemaVersion < 2)
            settings.HighQualityBluetoothAudioEnabled = false;
        UserSettingsStore.NormalizeRxAudioProcessingOptions(settings, storedSchemaVersion < 3);
        settings.RxJitterBuffer = RxJitterBufferSetting.Normalize(settings.RxJitterBuffer);
        settings.RxJitterBuffersBySystem = UserSettingsStore.NormalizeRxJitterBuffersBySystem(
            settings.RxJitterBuffersBySystem);
        settings.SchemaVersion = UserSettings.CurrentSchemaVersion;
        settings.TransmitEncryptionStates ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        settings.MainWindowPlacement = UserSettingsStore.NormalizeWindowPlacement(
            settings.MainWindowPlacement,
            defaultWidth: 1260,
            defaultHeight: 760,
            minimumWidth: 880,
            minimumHeight: 560,
            maximumWidth: 3840,
            maximumHeight: 2160);
        settings.CallHistoryWindowPlacement = UserSettingsStore.NormalizeWindowPlacement(settings.CallHistoryWindowPlacement);
        settings.GlobalPttKey = UserSettingsStore.NormalizeGlobalPttKey(settings.GlobalPttKey);
        settings.ActiveSystemPttKey = UserSettingsStore.NormalizeGlobalPttKey(settings.ActiveSystemPttKey);
        UserSettingsStore.ResolveDuplicateKeyboardPttKeys(settings);
        UserSettingsStore.NormalizeSerialPttSettings(settings);
        settings.UserBackgroundImage = string.IsNullOrWhiteSpace(settings.UserBackgroundImage)
            ? null
            : settings.UserBackgroundImage.Trim();
        settings.RecentCodeplugPaths = UserSettingsStore.NormalizeRecentCodeplugPaths(settings.RecentCodeplugPaths);
        settings.ToolbarClocks = UserSettingsStore.NormalizeToolbarClocks(settings.ToolbarClocks);
        UserSettingsStore.NormalizeUiSettings(settings);
        settings.ReceiveEnabledChannelKeys = UserSettingsStore.NormalizeNames(settings.ReceiveEnabledChannelKeys);
        settings.TransmitSelectedChannelKeys = UserSettingsStore.NormalizeNames(settings.TransmitSelectedChannelKeys);
        settings.ChannelWidgetPositions = UserSettingsStore.NormalizeWidgetPositions(settings.ChannelWidgetPositions);
        UserSettingsStore.NormalizeAudioInputSettings(settings);
        settings.AudioInputPresetName = settings.AudioInputPresetName?.Trim() ?? string.Empty;
        settings.AudioInputPresets = UserSettingsStore.NormalizeAudioInputPresets(settings.AudioInputPresets);
        settings.LastDtmfDigits = UserSettingsStore.NormalizeDtmfDigits(settings.LastDtmfDigits);
        settings.ToneFrequencyHz = UserSettingsStore.NormalizeToneFrequency(settings.ToneFrequencyHz);
        settings.ToneDurationSeconds = UserSettingsStore.NormalizeToneDuration(settings.ToneDurationSeconds);
        settings.QuickCallToneAFrequencyHz = UserSettingsStore.NormalizeToneFrequency(settings.QuickCallToneAFrequencyHz, 600);
        settings.QuickCallToneBFrequencyHz = UserSettingsStore.NormalizeToneFrequency(settings.QuickCallToneBFrequencyHz, 1200);
        settings.DtmfPresets = UserSettingsStore.NormalizeDtmfPresets(settings.DtmfPresets);
        settings.TonePresets = UserSettingsStore.NormalizeTonePresets(settings.TonePresets);
        settings.AlertTones = UserSettingsStore.NormalizeAlertTones(settings.AlertTones);
        settings.RecordingRetentionDays = Math.Max(0, settings.RecordingRetentionDays);
        settings.RecordingRootPath = UserSettingsStore.NormalizeRecordingRootPath(settings.RecordingRootPath);

        var channelVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> entry in settings.ChannelVolumes ?? [])
        {
            string channelKey = entry.Key?.Trim() ?? string.Empty;
            if (channelKey.Length > 0)
                channelVolumes[channelKey] = UserSettingsStore.NormalizeChannelVolume(entry.Value);
        }
        settings.ChannelVolumes = channelVolumes;
        settings.ChannelStereoBalances = UserSettingsStore.NormalizeChannelStereoBalances(settings.ChannelStereoBalances);

        var channelOutputDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in settings.ChannelOutputDeviceIds ?? [])
        {
            string channelKey = entry.Key?.Trim() ?? string.Empty;
            string deviceId = entry.Value?.Trim() ?? string.Empty;
            if (channelKey.Length > 0 && deviceId.Length > 0)
                channelOutputDevices[channelKey] = deviceId;
        }
        settings.ChannelOutputDeviceIds = channelOutputDevices;
        settings.WebStreamOutputDeviceIds = UserSettingsStore.NormalizeChannelOutputDevices(settings.WebStreamOutputDeviceIds);

        var webStreamVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> entry in settings.WebStreamVolumes ?? [])
        {
            string streamName = entry.Key?.Trim() ?? string.Empty;
            if (streamName.Length > 0)
                webStreamVolumes[streamName] = UserSettingsStore.NormalizeChannelVolume(entry.Value);
        }
        settings.WebStreamVolumes = webStreamVolumes;
        settings.RecordingEnabledChannelKeys = UserSettingsStore.NormalizeNames(settings.RecordingEnabledChannelKeys);

        var ignoredSubscribers = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<uint>> entry in settings.RecordingIgnoredSubscriberIds ?? [])
        {
            string channelKey = entry.Key?.Trim() ?? string.Empty;
            if (channelKey.Length == 0)
                continue;

            ignoredSubscribers[channelKey] = (entry.Value ?? [])
                .Where(subscriberId => subscriberId != 0)
                .Distinct()
                .ToList();
        }
        settings.RecordingIgnoredSubscriberIds = ignoredSubscribers;

        var memberships = new Dictionary<string, List<PatchMemberSetting>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<PatchMemberSetting>> entry in settings.PatchGroupMemberships ?? [])
        {
            string groupName = entry.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(groupName))
                continue;

            memberships[groupName] = (entry.Value ?? [])
                .Where(member => member is not null &&
                                 !string.IsNullOrWhiteSpace(member.SystemName) &&
                                 member.DestinationId != 0)
                .Select(member => new PatchMemberSetting
                {
                    SystemName = member.SystemName.Trim(),
                    DestinationId = member.DestinationId
                })
                .GroupBy(member => $"{member.SystemName.ToLowerInvariant()}|{member.DestinationId}")
                .Select(group => group.First())
                .ToList();
        }
        settings.PatchGroupMemberships = memberships;
        settings.PatchGroupModes = UserSettingsStore.NormalizeGroupStates(settings.PatchGroupModes);
        settings.PatchGroupEnabledStates = UserSettingsStore.NormalizeGroupStates(settings.PatchGroupEnabledStates);
        settings.SelectedWebStreams = UserSettingsStore.NormalizeNames(settings.SelectedWebStreams);
        return settings;
    }

    public void NormalizeBeforeWrite(UserSettings settings)
    {
        // Schema 1 defaulted this option on, so a stored true value does not
        // prove that the operator selected it. Require a fresh opt-in after
        // migration; schema 2 true values are always an explicit selection.
        if (settings.SchemaVersion < 2)
            settings.HighQualityBluetoothAudioEnabled = false;
        UserSettingsStore.NormalizeRxAudioProcessingOptions(settings, settings.SchemaVersion < 3);
        settings.RxJitterBuffer = RxJitterBufferSetting.Normalize(settings.RxJitterBuffer);
        settings.RxJitterBuffersBySystem = UserSettingsStore.NormalizeRxJitterBuffersBySystem(
            settings.RxJitterBuffersBySystem);
        settings.SchemaVersion = UserSettings.CurrentSchemaVersion;
        settings.DtmfPresets = UserSettingsStore.NormalizeDtmfPresets(settings.DtmfPresets);
        settings.TonePresets = UserSettingsStore.NormalizeTonePresets(settings.TonePresets);
        settings.MainWindowPlacement = UserSettingsStore.NormalizeWindowPlacement(
            settings.MainWindowPlacement,
            defaultWidth: 1260,
            defaultHeight: 760,
            minimumWidth: 880,
            minimumHeight: 560,
            maximumWidth: 3840,
            maximumHeight: 2160);
        settings.CallHistoryWindowPlacement = UserSettingsStore.NormalizeWindowPlacement(settings.CallHistoryWindowPlacement);
        settings.ToolbarClocks = UserSettingsStore.NormalizeToolbarClocks(settings.ToolbarClocks);
        UserSettingsStore.NormalizeUiSettings(settings);
        UserSettingsStore.NormalizeAudioInputSettings(settings);
        settings.RecentCodeplugPaths = UserSettingsStore.NormalizeRecentCodeplugPaths(settings.RecentCodeplugPaths);
        settings.AudioInputPresetName = settings.AudioInputPresetName?.Trim() ?? string.Empty;
        settings.AudioInputPresets = UserSettingsStore.NormalizeAudioInputPresets(settings.AudioInputPresets);
        settings.ChannelOutputDeviceIds = UserSettingsStore.NormalizeChannelOutputDevices(settings.ChannelOutputDeviceIds);
        settings.ChannelStereoBalances = UserSettingsStore.NormalizeChannelStereoBalances(settings.ChannelStereoBalances);
        settings.WebStreamOutputDeviceIds = UserSettingsStore.NormalizeChannelOutputDevices(settings.WebStreamOutputDeviceIds);
        settings.WebStreamVolumes = UserSettingsStore.NormalizeWebStreamVolumes(settings.WebStreamVolumes);
        settings.RecordingRootPath = UserSettingsStore.NormalizeRecordingRootPath(settings.RecordingRootPath);
        settings.RecordingEnabledChannelKeys = UserSettingsStore.NormalizeNames(settings.RecordingEnabledChannelKeys);
        settings.SelectedWebStreams = UserSettingsStore.NormalizeNames(settings.SelectedWebStreams);
        settings.GlobalPttKey = UserSettingsStore.NormalizeGlobalPttKey(settings.GlobalPttKey);
        settings.ActiveSystemPttKey = UserSettingsStore.NormalizeGlobalPttKey(settings.ActiveSystemPttKey);
        UserSettingsStore.ResolveDuplicateKeyboardPttKeys(settings);
        UserSettingsStore.NormalizeSerialPttSettings(settings);
        settings.ReceiveEnabledChannelKeys = UserSettingsStore.NormalizeNames(settings.ReceiveEnabledChannelKeys);
        settings.TransmitSelectedChannelKeys = UserSettingsStore.NormalizeNames(settings.TransmitSelectedChannelKeys);
        settings.ChannelWidgetPositions = UserSettingsStore.NormalizeWidgetPositions(settings.ChannelWidgetPositions);
    }
}
