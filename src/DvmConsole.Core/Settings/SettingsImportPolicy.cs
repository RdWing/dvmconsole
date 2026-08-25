namespace DvmConsole.Core.Settings;

internal static class SettingsImportPolicy
{
    public static SettingsImportPreview CreatePreview(string source, UserSettings settings)
    {
        var sections = new List<string>();
        if (settings.TalkPermitTone || !settings.ConnectionChimes || settings.DarkMode ||
            settings.UiFontSize != 14 || settings.UiScale != 1.0 ||
            settings.TogglePttMode || !string.Equals(settings.GlobalPttKey, "Space", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(settings.ActiveSystemPttKey, "None", StringComparison.OrdinalIgnoreCase) ||
            settings.SerialPttEnabled || settings.SerialPttActiveSystemOnly ||
            !string.IsNullOrWhiteSpace(settings.SerialPttPortName) ||
            settings.SerialPttBaudRate != 9_600 ||
            !settings.ShowSystemStatus || !settings.ShowChannels || !settings.ShowAlertTones ||
            !settings.LockWidgets || settings.ChannelWidgetPositions.Count > 0 ||
            !settings.ShowCallHistoryPane || settings.SnapCallHistoryToWindow ||
            !UserSettingsNormalizationRules.WindowPlacementsEqual(settings.MainWindowPlacement, new WindowPlacementSetting
            {
                Width = 1260,
                Height = 760
            }) ||
            !UserSettingsNormalizationRules.WindowPlacementsEqual(settings.CallHistoryWindowPlacement, new WindowPlacementSetting()) ||
            settings.UserBackgroundImage is not null)
        {
            sections.Add("General");
        }

        if (!string.Equals(settings.AudioInputDeviceId, "default", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(settings.AudioOutputDeviceId, "default", StringComparison.OrdinalIgnoreCase) ||
            UserSettingsNormalizationRules.HasCustomRxAudioProcessingOptions(settings.RxAudioProcessingOptions) ||
            !string.Equals(settings.AudioProcessingMode, UserSettings.DvmConsoleAudioProcessingMode, StringComparison.Ordinal) ||
            settings.HighQualityBluetoothAudioEnabled ||
            settings.AudioInputAgcEnabled || settings.AudioInputAgcTargetDbfs != -25.0 ||
            settings.AudioInputPresets.Count > 0 ||
            settings.ChannelVolumes.Count > 0 || settings.ChannelStereoBalances.Count > 0 ||
            settings.ChannelOutputDeviceIds.Count > 0 ||
            settings.WebStreamOutputDeviceIds.Count > 0 || settings.WebStreamVolumes.Count > 0)
        {
            sections.Add("Audio");
        }

        if (!UserSettingsNormalizationRules.RxJitterBufferSettingsEqual(settings.RxJitterBuffer, new RxJitterBufferSetting()) ||
            settings.RxJitterBuffersBySystem.Count > 0)
        {
            sections.Add("Connections");
        }

        if (settings.DtmfPresets.Count > 0 || settings.TonePresets.Count > 0 || settings.AlertTones.Count > 0 ||
            !string.Equals(settings.LastDtmfDigits, "123", StringComparison.Ordinal) ||
            settings.ToneFrequencyHz != 1000 || settings.ToneDurationSeconds != 1.0)
        {
            sections.Add("Presets");
        }

        if (settings.RecordingRetentionDays != 7 || !string.IsNullOrWhiteSpace(settings.RecordingRootPath) ||
            settings.RecordingEnabledChannelKeys.Count > 0 || settings.RecordingIgnoredSubscriberIds.Count > 0 ||
            settings.PatchGroupMemberships.Count > 0 || settings.PatchGroupModes.Count > 0 ||
            settings.PatchGroupEnabledStates.Count > 0 || settings.RetainPatchStateOnStartup)
        {
            sections.Add("Recording/patch");
        }

        if (!string.IsNullOrWhiteSpace(settings.LastCodeplugPath) || settings.RecentCodeplugPaths.Count > 0 ||
            !string.IsNullOrWhiteSpace(settings.LastSelectedSystemName) ||
            !string.IsNullOrWhiteSpace(settings.LastSelectedChannelKey) ||
            settings.ReceiveEnabledChannelKeys.Count > 0 ||
            settings.TransmitSelectedChannelKeys.Count > 0 || settings.SelectedWebStreams.Count > 0 ||
            settings.TransmitEncryptionStates.Count > 0)
        {
            sections.Add("Session");
        }

        return new SettingsImportPreview(source, settings.SchemaVersion, settings.LastCodeplugPath, sections);
    }

    public static void Merge(UserSettings target, UserSettings source, SettingsImportScope scope)
    {
        target.SchemaVersion = Math.Max(target.SchemaVersion, source.SchemaVersion);

        if ((scope & SettingsImportScope.General) != 0)
        {
            target.TogglePttMode = source.TogglePttMode;
            target.GlobalPttKey = source.GlobalPttKey;
            target.ActiveSystemPttKey = source.ActiveSystemPttKey;
            target.SerialPttEnabled = source.SerialPttEnabled;
            target.SerialPttActiveSystemOnly = source.SerialPttActiveSystemOnly;
            target.SerialPttPortName = source.SerialPttPortName;
            target.SerialPttBaudRate = source.SerialPttBaudRate;
            target.TalkPermitTone = source.TalkPermitTone;
            target.ConnectionChimes = source.ConnectionChimes;
            target.DarkMode = source.DarkMode;
            target.UiFontSize = source.UiFontSize;
            target.UiScale = source.UiScale;
            target.ClockUse24HourTime = source.ClockUse24HourTime;
            target.ClockShowSeconds = source.ClockShowSeconds;
            target.ToolbarClocks = source.ToolbarClocks.ToList();
            target.KeepWindowOnTop = source.KeepWindowOnTop;
            target.ShowSystemStatus = source.ShowSystemStatus;
            target.ShowChannels = source.ShowChannels;
            target.ShowAlertTones = source.ShowAlertTones;
            target.LockWidgets = source.LockWidgets;
            target.ChannelWidgetPositions = source.ChannelWidgetPositions.ToDictionary(
                entry => entry.Key,
                entry => new WidgetPositionSetting { X = entry.Value.X, Y = entry.Value.Y },
                StringComparer.OrdinalIgnoreCase);
            target.UserBackgroundImage = source.UserBackgroundImage;
            target.ShowCallHistoryPane = source.ShowCallHistoryPane;
            target.SnapCallHistoryToWindow = source.SnapCallHistoryToWindow;
            target.MainWindowPlacement = UserSettingsNormalizationRules.CopyWindowPlacement(source.MainWindowPlacement);
            target.CallHistoryWindowPlacement = new WindowPlacementSetting
            {
                Left = source.CallHistoryWindowPlacement.Left,
                Top = source.CallHistoryWindowPlacement.Top,
                Width = source.CallHistoryWindowPlacement.Width,
                Height = source.CallHistoryWindowPlacement.Height
            };
        }

        if ((scope & SettingsImportScope.Audio) != 0)
        {
            target.AudioInputDeviceId = source.AudioInputDeviceId;
            target.AudioOutputDeviceId = source.AudioOutputDeviceId;
            target.RxAudioProcessingOptions = source.RxAudioProcessingOptions.ToDictionary(
                entry => entry.Key,
                entry => UserSettingsNormalizationRules.NormalizeRxAudioProcessingMode(entry.Value),
                StringComparer.OrdinalIgnoreCase);
            target.AudioProcessingMode = source.AudioProcessingMode;
            target.HighQualityBluetoothAudioEnabled = source.HighQualityBluetoothAudioEnabled;
            target.AudioInputAgcEnabled = source.AudioInputAgcEnabled;
            target.AudioInputAgcTargetDbfs = source.AudioInputAgcTargetDbfs;
            target.KeepTransmitMicrophoneWarm = source.KeepTransmitMicrophoneWarm;
            target.AudioInputGain = source.AudioInputGain;
            target.AudioInputEqLowGainDb = source.AudioInputEqLowGainDb;
            target.AudioInputEqMidGainDb = source.AudioInputEqMidGainDb;
            target.AudioInputEqHighGainDb = source.AudioInputEqHighGainDb;
            target.AudioInputPresetName = source.AudioInputPresetName;
            target.AudioInputPresets = source.AudioInputPresets.ToList();
            target.MuteRxAudioWhileTransmitting = source.MuteRxAudioWhileTransmitting;
            target.ChannelVolumes = new Dictionary<string, double>(source.ChannelVolumes, StringComparer.OrdinalIgnoreCase);
            target.ChannelStereoBalances = new Dictionary<string, double>(source.ChannelStereoBalances, StringComparer.OrdinalIgnoreCase);
            target.ChannelOutputDeviceIds = new Dictionary<string, string>(source.ChannelOutputDeviceIds, StringComparer.OrdinalIgnoreCase);
            target.WebStreamOutputDeviceIds = new Dictionary<string, string>(source.WebStreamOutputDeviceIds, StringComparer.OrdinalIgnoreCase);
            target.WebStreamVolumes = new Dictionary<string, double>(source.WebStreamVolumes, StringComparer.OrdinalIgnoreCase);
        }

        if ((scope & SettingsImportScope.Connections) != 0)
        {
            target.RxJitterBuffer = RxJitterBufferSetting.Normalize(source.RxJitterBuffer);
            target.RxJitterBuffersBySystem = UserSettingsNormalizationRules.NormalizeRxJitterBuffersBySystem(
                source.RxJitterBuffersBySystem);
        }

        if ((scope & SettingsImportScope.Presets) != 0)
        {
            target.LastDtmfDigits = source.LastDtmfDigits;
            target.ToneFrequencyHz = source.ToneFrequencyHz;
            target.ToneDurationSeconds = source.ToneDurationSeconds;
            target.QuickCallToneAFrequencyHz = source.QuickCallToneAFrequencyHz;
            target.QuickCallToneBFrequencyHz = source.QuickCallToneBFrequencyHz;
            target.DtmfPresets = source.DtmfPresets.ToList();
            target.TonePresets = source.TonePresets.ToList();
            target.AlertTones = source.AlertTones.ToList();
        }

        if ((scope & SettingsImportScope.RecordingAndPatch) != 0)
        {
            target.RecordingRetentionDays = source.RecordingRetentionDays;
            target.RecordingRootPath = source.RecordingRootPath;
            target.RecordingEnabledChannelKeys = source.RecordingEnabledChannelKeys.ToList();
            target.RecordingIgnoredSubscriberIds = source.RecordingIgnoredSubscriberIds
                .ToDictionary(entry => entry.Key, entry => entry.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            target.PatchGroupMemberships = source.PatchGroupMemberships
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.Select(member => new PatchMemberSetting
                    {
                        SystemName = member.SystemName,
                        DestinationId = member.DestinationId,
                        ChannelName = member.ChannelName
                    }).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            target.PatchGroupModes = new Dictionary<string, bool>(source.PatchGroupModes, StringComparer.OrdinalIgnoreCase);
            target.PatchGroupEnabledStates = new Dictionary<string, bool>(source.PatchGroupEnabledStates, StringComparer.OrdinalIgnoreCase);
            target.RetainPatchStateOnStartup = source.RetainPatchStateOnStartup;
        }

        if ((scope & SettingsImportScope.Session) != 0)
        {
            target.LastCodeplugPath = source.LastCodeplugPath;
            target.RecentCodeplugPaths = source.RecentCodeplugPaths.ToList();
            target.LastSelectedSystemName = source.LastSelectedSystemName;
            target.LastSelectedChannelKey = source.LastSelectedChannelKey;
            target.ReceiveEnabledChannelKeys = source.ReceiveEnabledChannelKeys.ToList();
            target.TransmitSelectedChannelKeys = source.TransmitSelectedChannelKeys.ToList();
            target.SelectedWebStreams = source.SelectedWebStreams.ToList();
            target.TransmitEncryptionStates = new Dictionary<string, bool>(source.TransmitEncryptionStates, StringComparer.OrdinalIgnoreCase);
        }
    }
}
