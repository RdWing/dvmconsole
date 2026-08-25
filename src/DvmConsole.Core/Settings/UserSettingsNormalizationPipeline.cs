namespace DvmConsole.Core.Settings;

internal sealed class UserSettingsNormalizationPipeline
{
    public UserSettings NormalizeAfterLoad(UserSettings settings)
    {
        int storedSchemaVersion = settings.SchemaVersion;
        if (storedSchemaVersion < 2)
            settings.HighQualityBluetoothAudioEnabled = false;
        UserSettingsNormalizationRules.NormalizeRxAudioProcessingOptions(settings, storedSchemaVersion < 3);
        settings.RxJitterBuffer = RxJitterBufferSetting.Normalize(settings.RxJitterBuffer);
        settings.RxJitterBuffersBySystem = UserSettingsNormalizationRules.NormalizeRxJitterBuffersBySystem(
            settings.RxJitterBuffersBySystem);
        settings.SchemaVersion = UserSettings.CurrentSchemaVersion;
        settings.TransmitEncryptionStates ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        settings.MainWindowPlacement = UserSettingsNormalizationRules.NormalizeWindowPlacement(
            settings.MainWindowPlacement,
            defaultWidth: 1260,
            defaultHeight: 760,
            minimumWidth: 880,
            minimumHeight: 560,
            maximumWidth: 3840,
            maximumHeight: 2160);
        settings.CallHistoryWindowPlacement = UserSettingsNormalizationRules.NormalizeWindowPlacement(settings.CallHistoryWindowPlacement);
        settings.GlobalPttKey = UserSettingsNormalizationRules.NormalizeGlobalPttKey(settings.GlobalPttKey);
        settings.ActiveSystemPttKey = UserSettingsNormalizationRules.NormalizeGlobalPttKey(settings.ActiveSystemPttKey);
        UserSettingsNormalizationRules.ResolveDuplicateKeyboardPttKeys(settings);
        UserSettingsNormalizationRules.NormalizeSerialPttSettings(settings);
        settings.UserBackgroundImage = string.IsNullOrWhiteSpace(settings.UserBackgroundImage)
            ? null
            : settings.UserBackgroundImage.Trim();
        settings.RecentCodeplugPaths = UserSettingsNormalizationRules.NormalizeRecentCodeplugPaths(settings.RecentCodeplugPaths);
        settings.ToolbarClocks = UserSettingsNormalizationRules.NormalizeToolbarClocks(settings.ToolbarClocks);
        UserSettingsNormalizationRules.NormalizeUiSettings(settings);
        settings.ReceiveEnabledChannelKeys = UserSettingsNormalizationRules.NormalizeNames(settings.ReceiveEnabledChannelKeys);
        settings.TransmitSelectedChannelKeys = UserSettingsNormalizationRules.NormalizeNames(settings.TransmitSelectedChannelKeys);
        settings.ChannelWidgetPositions = UserSettingsNormalizationRules.NormalizeWidgetPositions(settings.ChannelWidgetPositions);
        UserSettingsNormalizationRules.NormalizeAudioInputSettings(settings);
        settings.AudioInputPresetName = settings.AudioInputPresetName?.Trim() ?? string.Empty;
        settings.AudioInputPresets = UserSettingsNormalizationRules.NormalizeAudioInputPresets(settings.AudioInputPresets);
        settings.LastDtmfDigits = UserSettingsNormalizationRules.NormalizeDtmfDigits(settings.LastDtmfDigits);
        settings.ToneFrequencyHz = UserSettingsNormalizationRules.NormalizeToneFrequency(settings.ToneFrequencyHz);
        settings.ToneDurationSeconds = UserSettingsNormalizationRules.NormalizeToneDuration(settings.ToneDurationSeconds);
        settings.QuickCallToneAFrequencyHz = UserSettingsNormalizationRules.NormalizeToneFrequency(settings.QuickCallToneAFrequencyHz, 600);
        settings.QuickCallToneBFrequencyHz = UserSettingsNormalizationRules.NormalizeToneFrequency(settings.QuickCallToneBFrequencyHz, 1200);
        settings.DtmfPresets = UserSettingsNormalizationRules.NormalizeDtmfPresets(settings.DtmfPresets);
        settings.TonePresets = UserSettingsNormalizationRules.NormalizeTonePresets(settings.TonePresets);
        settings.AlertTones = UserSettingsNormalizationRules.NormalizeAlertTones(settings.AlertTones);
        settings.RecordingRetentionDays = Math.Max(0, settings.RecordingRetentionDays);
        settings.RecordingRootPath = UserSettingsNormalizationRules.NormalizeRecordingRootPath(settings.RecordingRootPath);

        var channelVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> entry in settings.ChannelVolumes ?? [])
        {
            string channelKey = entry.Key?.Trim() ?? string.Empty;
            if (channelKey.Length > 0)
                channelVolumes[channelKey] = UserSettingsNormalizationRules.NormalizeChannelVolume(entry.Value);
        }
        settings.ChannelVolumes = channelVolumes;
        settings.ChannelStereoBalances = UserSettingsNormalizationRules.NormalizeChannelStereoBalances(settings.ChannelStereoBalances);

        var channelOutputDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in settings.ChannelOutputDeviceIds ?? [])
        {
            string channelKey = entry.Key?.Trim() ?? string.Empty;
            string deviceId = entry.Value?.Trim() ?? string.Empty;
            if (channelKey.Length > 0 && deviceId.Length > 0)
                channelOutputDevices[channelKey] = deviceId;
        }
        settings.ChannelOutputDeviceIds = channelOutputDevices;
        settings.WebStreamOutputDeviceIds = UserSettingsNormalizationRules.NormalizeChannelOutputDevices(settings.WebStreamOutputDeviceIds);

        var webStreamVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> entry in settings.WebStreamVolumes ?? [])
        {
            string streamName = entry.Key?.Trim() ?? string.Empty;
            if (streamName.Length > 0)
                webStreamVolumes[streamName] = UserSettingsNormalizationRules.NormalizeChannelVolume(entry.Value);
        }
        settings.WebStreamVolumes = webStreamVolumes;
        settings.RecordingEnabledChannelKeys = UserSettingsNormalizationRules.NormalizeNames(settings.RecordingEnabledChannelKeys);

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
                    DestinationId = member.DestinationId,
                    ChannelName = string.IsNullOrWhiteSpace(member.ChannelName)
                        ? null
                        : member.ChannelName.Trim()
                })
                .GroupBy(member => new Runtime.PatchMemberAddress(
                    member.SystemName,
                    member.DestinationId,
                    member.ChannelName).Key)
                .Select(group => group.First())
                .ToList();
        }
        settings.PatchGroupMemberships = memberships;
        settings.PatchGroupModes = UserSettingsNormalizationRules.NormalizeGroupStates(settings.PatchGroupModes);
        settings.PatchGroupEnabledStates = UserSettingsNormalizationRules.NormalizeGroupStates(settings.PatchGroupEnabledStates);
        settings.SelectedWebStreams = UserSettingsNormalizationRules.NormalizeNames(settings.SelectedWebStreams);
        return settings;
    }

    public void NormalizeBeforeWrite(UserSettings settings)
    {
        // Schema 1 defaulted this option on, so a stored true value does not
        // prove that the operator selected it. Require a fresh opt-in after
        // migration; schema 2 true values are always an explicit selection.
        if (settings.SchemaVersion < 2)
            settings.HighQualityBluetoothAudioEnabled = false;
        UserSettingsNormalizationRules.NormalizeRxAudioProcessingOptions(settings, settings.SchemaVersion < 3);
        settings.RxJitterBuffer = RxJitterBufferSetting.Normalize(settings.RxJitterBuffer);
        settings.RxJitterBuffersBySystem = UserSettingsNormalizationRules.NormalizeRxJitterBuffersBySystem(
            settings.RxJitterBuffersBySystem);
        settings.SchemaVersion = UserSettings.CurrentSchemaVersion;
        settings.DtmfPresets = UserSettingsNormalizationRules.NormalizeDtmfPresets(settings.DtmfPresets);
        settings.TonePresets = UserSettingsNormalizationRules.NormalizeTonePresets(settings.TonePresets);
        settings.MainWindowPlacement = UserSettingsNormalizationRules.NormalizeWindowPlacement(
            settings.MainWindowPlacement,
            defaultWidth: 1260,
            defaultHeight: 760,
            minimumWidth: 880,
            minimumHeight: 560,
            maximumWidth: 3840,
            maximumHeight: 2160);
        settings.CallHistoryWindowPlacement = UserSettingsNormalizationRules.NormalizeWindowPlacement(settings.CallHistoryWindowPlacement);
        settings.ToolbarClocks = UserSettingsNormalizationRules.NormalizeToolbarClocks(settings.ToolbarClocks);
        UserSettingsNormalizationRules.NormalizeUiSettings(settings);
        UserSettingsNormalizationRules.NormalizeAudioInputSettings(settings);
        settings.RecentCodeplugPaths = UserSettingsNormalizationRules.NormalizeRecentCodeplugPaths(settings.RecentCodeplugPaths);
        settings.AudioInputPresetName = settings.AudioInputPresetName?.Trim() ?? string.Empty;
        settings.AudioInputPresets = UserSettingsNormalizationRules.NormalizeAudioInputPresets(settings.AudioInputPresets);
        settings.ChannelOutputDeviceIds = UserSettingsNormalizationRules.NormalizeChannelOutputDevices(settings.ChannelOutputDeviceIds);
        settings.ChannelStereoBalances = UserSettingsNormalizationRules.NormalizeChannelStereoBalances(settings.ChannelStereoBalances);
        settings.WebStreamOutputDeviceIds = UserSettingsNormalizationRules.NormalizeChannelOutputDevices(settings.WebStreamOutputDeviceIds);
        settings.WebStreamVolumes = UserSettingsNormalizationRules.NormalizeWebStreamVolumes(settings.WebStreamVolumes);
        settings.RecordingRootPath = UserSettingsNormalizationRules.NormalizeRecordingRootPath(settings.RecordingRootPath);
        settings.RecordingEnabledChannelKeys = UserSettingsNormalizationRules.NormalizeNames(settings.RecordingEnabledChannelKeys);
        settings.SelectedWebStreams = UserSettingsNormalizationRules.NormalizeNames(settings.SelectedWebStreams);
        settings.GlobalPttKey = UserSettingsNormalizationRules.NormalizeGlobalPttKey(settings.GlobalPttKey);
        settings.ActiveSystemPttKey = UserSettingsNormalizationRules.NormalizeGlobalPttKey(settings.ActiveSystemPttKey);
        UserSettingsNormalizationRules.ResolveDuplicateKeyboardPttKeys(settings);
        UserSettingsNormalizationRules.NormalizeSerialPttSettings(settings);
        settings.ReceiveEnabledChannelKeys = UserSettingsNormalizationRules.NormalizeNames(settings.ReceiveEnabledChannelKeys);
        settings.TransmitSelectedChannelKeys = UserSettingsNormalizationRules.NormalizeNames(settings.TransmitSelectedChannelKeys);
        settings.ChannelWidgetPositions = UserSettingsNormalizationRules.NormalizeWidgetPositions(settings.ChannelWidgetPositions);
    }
}
