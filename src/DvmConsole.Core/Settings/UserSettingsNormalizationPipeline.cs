namespace DvmConsole.Core.Settings;

internal sealed class UserSettingsNormalizationPipeline
{
    public UserSettings NormalizeAfterLoad(UserSettings settings)
    {
        int storedSchemaVersion = settings.SchemaVersion;
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
        settings.UserBackgroundAssetId = NormalizeGuid(settings.UserBackgroundAssetId);
        settings.RecentCodeplugPaths = UserSettingsNormalizationRules.NormalizeRecentCodeplugPaths(settings.RecentCodeplugPaths);
        settings.ToolbarClocks = UserSettingsNormalizationRules.NormalizeToolbarClocks(settings.ToolbarClocks);
        UserSettingsNormalizationRules.NormalizeUiSettings(settings);
        settings.ReceiveEnabledChannelKeys = UserSettingsNormalizationRules.NormalizeNames(settings.ReceiveEnabledChannelKeys);
        settings.TransmitSelectedChannelKeys = UserSettingsNormalizationRules.NormalizeNames(settings.TransmitSelectedChannelKeys);
        settings.ChannelWidgetPositions = UserSettingsNormalizationRules.NormalizeWidgetPositions(settings.ChannelWidgetPositions);
        settings.CodeplugStudioStates = NormalizeCodeplugStudioStates(settings.CodeplugStudioStates);
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
        settings.CodeplugGroupStates = NormalizeCodeplugGroupStates(settings.CodeplugGroupStates);
        settings.ConfigurationOperatorStates = NormalizeConfigurationOperatorStates(
            settings.ConfigurationOperatorStates);
        settings.ActiveConfigurationOperatorStateId = NormalizeGuid(
            settings.ActiveConfigurationOperatorStateId);
        settings.SelectedWebStreams = UserSettingsNormalizationRules.NormalizeNames(settings.SelectedWebStreams);
        return settings;
    }

    public void NormalizeBeforeWrite(UserSettings settings)
    {
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
        settings.CodeplugStudioStates = NormalizeCodeplugStudioStates(settings.CodeplugStudioStates);
        settings.CodeplugGroupStates = NormalizeCodeplugGroupStates(settings.CodeplugGroupStates);
        settings.ConfigurationOperatorStates = NormalizeConfigurationOperatorStates(
            settings.ConfigurationOperatorStates);
        settings.ActiveConfigurationOperatorStateId = NormalizeGuid(
            settings.ActiveConfigurationOperatorStateId);
        settings.UserBackgroundAssetId = NormalizeGuid(settings.UserBackgroundAssetId);
    }

    private static string? NormalizeGuid(string? value)
        => Guid.TryParse(value?.Trim(), out Guid parsed) ? parsed.ToString("N") : null;

    private static Dictionary<string, CodeplugStudioState> NormalizeCodeplugStudioStates(
        Dictionary<string, CodeplugStudioState>? states)
    {
        var normalized = new Dictionary<string, CodeplugStudioState>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, CodeplugStudioState> entry in states ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null)
                continue;

            string path;
            try
            {
                path = CodeplugGroupStateStore.NormalizePath(entry.Key);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> assignment in entry.Value.ZoneSystemAssignments ?? [])
            {
                string zone = assignment.Key?.Trim() ?? string.Empty;
                string system = assignment.Value?.Trim() ?? string.Empty;
                if (zone.Length > 0 && system.Length > 0)
                    assignments[zone] = system;
            }
            normalized[path] = new CodeplugStudioState
            {
                ZoneSystemAssignments = assignments,
                CallPrioritySystemNames = UserSettingsNormalizationRules.NormalizeNames(
                    entry.Value.CallPrioritySystemNames)
            };
        }
        return normalized;
    }

    private static Dictionary<string, CodeplugGroupState> NormalizeCodeplugGroupStates(
        Dictionary<string, CodeplugGroupState>? states)
    {
        var normalized = new Dictionary<string, CodeplugGroupState>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, CodeplugGroupState> entry in states ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null)
                continue;

            string path;
            try
            {
                path = CodeplugGroupStateStore.NormalizePath(entry.Key);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            CodeplugGroupState state = entry.Value;
            var memberships = new Dictionary<string, List<PatchMemberSetting>>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<PatchMemberSetting>> membership in state.Memberships ?? [])
            {
                string groupName = membership.Key?.Trim() ?? string.Empty;
                if (groupName.Length == 0)
                    continue;
                memberships[groupName] = (membership.Value ?? [])
                    .Where(member => member is not null &&
                                     !string.IsNullOrWhiteSpace(member.SystemName) &&
                                     member.DestinationId != 0)
                    .Select(member => new PatchMemberSetting
                    {
                        SystemName = member.SystemName.Trim(),
                        DestinationId = member.DestinationId,
                        ChannelName = string.IsNullOrWhiteSpace(member.ChannelName) ? null : member.ChannelName.Trim()
                    })
                    .GroupBy(member => new Runtime.PatchMemberAddress(
                        member.SystemName,
                        member.DestinationId,
                        member.ChannelName).Key)
                    .Select(group => group.First())
                    .ToList();
            }

            normalized[path] = new CodeplugGroupState
            {
                Memberships = memberships,
                OneWayModes = UserSettingsNormalizationRules.NormalizeGroupStates(state.OneWayModes),
                EnabledStates = UserSettingsNormalizationRules.NormalizeGroupStates(state.EnabledStates)
            };
        }
        return normalized;
    }

    private static Dictionary<string, ConfigurationOperatorState> NormalizeConfigurationOperatorStates(
        Dictionary<string, ConfigurationOperatorState>? states)
    {
        var normalized = new Dictionary<string, ConfigurationOperatorState>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, ConfigurationOperatorState> entry in states ?? [])
        {
            string? id = NormalizeGuid(entry.Key);
            if (id is null || entry.Value is null)
                continue;

            ConfigurationOperatorState state = entry.Value.Clone();
            state.ChannelWidgetPositions = UserSettingsNormalizationRules.NormalizeWidgetPositions(
                state.ChannelWidgetPositions);
            state.ReceiveEnabledChannelKeys = UserSettingsNormalizationRules.NormalizeNames(
                state.ReceiveEnabledChannelKeys);
            state.TransmitSelectedChannelKeys = UserSettingsNormalizationRules.NormalizeNames(
                state.TransmitSelectedChannelKeys);
            state.ChannelVolumes = state.ChannelVolumes
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .ToDictionary(
                    item => item.Key.Trim(),
                    item => UserSettingsNormalizationRules.NormalizeChannelVolume(item.Value),
                    StringComparer.OrdinalIgnoreCase);
            state.ChannelStereoBalances = UserSettingsNormalizationRules.NormalizeChannelStereoBalances(
                state.ChannelStereoBalances);
            state.ChannelOutputDeviceIds = UserSettingsNormalizationRules.NormalizeChannelOutputDevices(
                state.ChannelOutputDeviceIds);
            state.WebStreamOutputDeviceIds = UserSettingsNormalizationRules.NormalizeChannelOutputDevices(
                state.WebStreamOutputDeviceIds);
            state.WebStreamVolumes = UserSettingsNormalizationRules.NormalizeWebStreamVolumes(
                state.WebStreamVolumes);
            state.RecordingEnabledChannelKeys = UserSettingsNormalizationRules.NormalizeNames(
                state.RecordingEnabledChannelKeys);
            state.RecordingIgnoredSubscriberIds = state.RecordingIgnoredSubscriberIds
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .ToDictionary(
                    item => item.Key.Trim(),
                    item => (item.Value ?? []).Where(value => value != 0).Distinct().ToList(),
                    StringComparer.OrdinalIgnoreCase);
            state.GroupState = NormalizeConfigurationGroupState(state.GroupState);
            state.StudioState = NormalizeConfigurationStudioState(state.StudioState);
            state.SelectedWebStreams = UserSettingsNormalizationRules.NormalizeNames(state.SelectedWebStreams);
            state.TransmitEncryptionStates = new Dictionary<string, bool>(
                state.TransmitEncryptionStates ?? [],
                StringComparer.OrdinalIgnoreCase);
            state.LastSelectedSystemName = string.IsNullOrWhiteSpace(state.LastSelectedSystemName)
                ? null
                : state.LastSelectedSystemName.Trim();
            state.LastSelectedChannelKey = string.IsNullOrWhiteSpace(state.LastSelectedChannelKey)
                ? null
                : state.LastSelectedChannelKey.Trim();
            normalized[id] = state;
        }
        return normalized;
    }

    private static CodeplugStudioState NormalizeConfigurationStudioState(CodeplugStudioState? state)
    {
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> assignment in state?.ZoneSystemAssignments ?? [])
        {
            string zone = assignment.Key?.Trim() ?? string.Empty;
            string system = assignment.Value?.Trim() ?? string.Empty;
            if (zone.Length > 0 && system.Length > 0)
                assignments[zone] = system;
        }
        return new CodeplugStudioState
        {
            ZoneSystemAssignments = assignments,
            CallPrioritySystemNames = UserSettingsNormalizationRules.NormalizeNames(
                state?.CallPrioritySystemNames)
        };
    }

    private static CodeplugGroupState NormalizeConfigurationGroupState(CodeplugGroupState? state)
    {
        state ??= new CodeplugGroupState();
        var memberships = new Dictionary<string, List<PatchMemberSetting>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<PatchMemberSetting>> membership in state.Memberships ?? [])
        {
            string groupName = membership.Key?.Trim() ?? string.Empty;
            if (groupName.Length == 0)
                continue;
            memberships[groupName] = (membership.Value ?? [])
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
        return new CodeplugGroupState
        {
            Memberships = memberships,
            OneWayModes = UserSettingsNormalizationRules.NormalizeGroupStates(state.OneWayModes),
            EnabledStates = UserSettingsNormalizationRules.NormalizeGroupStates(state.EnabledStates)
        };
    }
}
