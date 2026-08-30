namespace DvmConsole.Core.Settings;

internal static class SettingsImportPolicy
{
    private static readonly (SettingsImportScope Scope, string Name)[] PreviewSections =
    [
        (SettingsImportScope.General, "General"),
        (SettingsImportScope.Audio, "Audio"),
        (SettingsImportScope.Connections, "Connections"),
        (SettingsImportScope.Presets, "Presets"),
        (SettingsImportScope.RecordingAndPatch, "Recording/patch"),
        (SettingsImportScope.Session, "Session")
    ];

    public static SettingsImportPreview CreatePreview(string source, UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string[] sections = PreviewSections
            .Where(section => HasNonDefaultValues(settings, section.Scope))
            .Select(section => section.Name)
            .ToArray();

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
            target.LocalToneMonitorEnabled = source.LocalToneMonitorEnabled;
            target.VerboseLoggingEnabled = source.VerboseLoggingEnabled;
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
            target.CodeplugStudioStates = source.CodeplugStudioStates.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
            target.UserBackgroundImage = source.UserBackgroundImage;
            target.ShowCallHistoryPane = source.ShowCallHistoryPane;
            target.SnapCallHistoryToWindow = source.SnapCallHistoryToWindow;
            target.RestoreSelectedChannelsOnStartup = source.RestoreSelectedChannelsOnStartup;
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
            target.CodeplugGroupStates = source.CodeplugGroupStates.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
            target.LegacyPatchGroupStateMigrated = source.LegacyPatchGroupStateMigrated;
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

    private static bool HasNonDefaultValues(UserSettings source, SettingsImportScope scope)
    {
        var baseline = new UserSettings { SchemaVersion = source.SchemaVersion };
        var imported = new UserSettings { SchemaVersion = source.SchemaVersion };
        Merge(imported, source, scope);
        var serializer = new UserSettingsSerializer();
        return !string.Equals(
            serializer.Serialize(baseline),
            serializer.Serialize(imported),
            StringComparison.Ordinal);
    }
}
