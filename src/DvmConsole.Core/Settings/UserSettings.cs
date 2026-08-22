using System.Text.Json;
using System.Text.Json.Serialization;

namespace DvmConsole.Core.Settings;

// Portable window placement. Coordinates are optional because a display
// topology can change between launches.
public sealed class WindowPlacementSetting
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 560;
    public double Height { get; set; } = 500;
}

// Portable position for a movable console widget.
public sealed class WidgetPositionSetting
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class RxAudioProcessingModeSetting
{
    public const string DmrMode = "Dmr";
    public const string P25Phase1Mode = "P25Phase1";
    public const string NxdnMode = "Nxdn";
    public const string P25Phase2Mode = "P25Phase2";

    public static IReadOnlyList<string> ModeKeys { get; } =
        [DmrMode, P25Phase1Mode, NxdnMode, P25Phase2Mode];

    public bool HighPassFilterEnabled { get; set; } = true;
    public double HighPassFrequencyHz { get; set; } = 250;
    public bool PeakingFilterEnabled { get; set; } = true;
    public double PeakingFrequencyHz { get; set; } = 2_500;
    public double PeakingGainDb { get; set; } = 3;
    public bool CompressorEnabled { get; set; }
    public double CompressorRatio { get; set; } = 3;
    public double CompressorThresholdDbfs { get; set; } = -18;
    public double CompressorMakeupGainDb { get; set; } = 3;

    public static Dictionary<string, RxAudioProcessingModeSetting> CreateDefaults()
        => ModeKeys.ToDictionary(
            key => key,
            _ => new RxAudioProcessingModeSetting(),
            StringComparer.OrdinalIgnoreCase);
}

public sealed class RxJitterBufferSetting
{
    public const int DefaultP25Milliseconds = 180;
    public const int DefaultDmrMilliseconds = 120;
    public const int DefaultNxdnMilliseconds = 160;
    public const int MaximumP25Milliseconds = 1_620;
    public const int MaximumDmrMilliseconds = 540;
    public const int MaximumNxdnMilliseconds = 720;

    public static IReadOnlyList<int> P25OptionsMilliseconds { get; } =
        [0, 180, 360, 540, 720, 900, 1_080, 1_260, 1_440, MaximumP25Milliseconds];
    public static IReadOnlyList<int> DmrOptionsMilliseconds { get; } =
        [0, 60, 120, 180, 240, 300, 360, 420, 480, MaximumDmrMilliseconds];
    public static IReadOnlyList<int> NxdnOptionsMilliseconds { get; } =
        [0, 80, 160, 240, 320, 400, 480, 560, 640, MaximumNxdnMilliseconds];

    public int P25Milliseconds { get; set; } = DefaultP25Milliseconds;
    public int DmrMilliseconds { get; set; } = DefaultDmrMilliseconds;
    public int NxdnMilliseconds { get; set; } = DefaultNxdnMilliseconds;
    public bool P25Adaptive { get; set; } = true;
    public bool DmrAdaptive { get; set; } = true;
    public bool NxdnAdaptive { get; set; } = true;

    public static RxJitterBufferSetting Normalize(RxJitterBufferSetting? setting)
    {
        setting ??= new RxJitterBufferSetting();
        return new RxJitterBufferSetting
        {
            P25Milliseconds = NormalizeChoice(
                setting.P25Milliseconds,
                P25OptionsMilliseconds,
                DefaultP25Milliseconds),
            DmrMilliseconds = NormalizeChoice(
                setting.DmrMilliseconds,
                DmrOptionsMilliseconds,
                DefaultDmrMilliseconds),
            NxdnMilliseconds = NormalizeChoice(
                setting.NxdnMilliseconds,
                NxdnOptionsMilliseconds,
                DefaultNxdnMilliseconds),
            P25Adaptive = setting.P25Adaptive,
            DmrAdaptive = setting.DmrAdaptive,
            NxdnAdaptive = setting.NxdnAdaptive
        };
    }

    private static int NormalizeChoice(
        int value,
        IReadOnlyList<int> choices,
        int fallback)
        => choices.Contains(value) ? value : fallback;
}

// Small, portable subset of operator state that is safe to persist outside a
// codeplug. Protocol credentials and encryption keys remain codeplug-owned.
public sealed class UserSettings
{
    public const int CurrentSchemaVersion = 6;
    public const string DvmConsoleAudioProcessingMode = "DvmConsole";
    public const string AppleVoiceProcessingMode = "AppleVoiceProcessing";
    public const int MaximumToolbarClocks = 8;
    public const int MaximumRecentCodeplugs = 8;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string? LastCodeplugPath { get; set; }
    public List<string> RecentCodeplugPaths { get; set; } = [];
    public string? LastSelectedSystemName { get; set; }
    public string? LastSelectedChannelKey { get; set; }
    public string AudioInputDeviceId { get; set; } = "default";
    public string AudioOutputDeviceId { get; set; } = "default";
    public Dictionary<string, RxAudioProcessingModeSetting> RxAudioProcessingOptions { get; set; }
        = RxAudioProcessingModeSetting.CreateDefaults();
    // Retained as the migration/default value for settings written before
    // per-connection jitter configuration was introduced.
    public RxJitterBufferSetting RxJitterBuffer { get; set; } = new();
    public Dictionary<string, RxJitterBufferSetting> RxJitterBuffersBySystem { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("RxAudioProcessingEnabled")]
    public bool? LegacyRxAudioProcessingEnabled { get; set; }
    public string AudioProcessingMode { get; set; } = DvmConsoleAudioProcessingMode;
    public bool HighQualityBluetoothAudioEnabled { get; set; }
    public bool AudioInputAgcEnabled { get; set; }
    public double AudioInputAgcTargetDbfs { get; set; } = -25.0;
    public bool KeepTransmitMicrophoneWarm { get; set; } = false;
    public double AudioInputGain { get; set; } = 1.0;
    public double AudioInputEqLowGainDb { get; set; }
    public double AudioInputEqMidGainDb { get; set; }
    public double AudioInputEqHighGainDb { get; set; }
    public string AudioInputPresetName { get; set; } = string.Empty;
    public List<AudioInputPresetSetting> AudioInputPresets { get; set; } = [];
    public bool MuteRxAudioWhileTransmitting { get; set; } = true;
    public bool TalkPermitTone { get; set; }
    public bool ConnectionChimes { get; set; } = true;
    public bool DarkMode { get; set; }
    public double UiFontSize { get; set; } = 14;
    public double UiScale { get; set; } = 1.0;
    public bool ClockUse24HourTime { get; set; } = true;
    public bool ClockShowSeconds { get; set; } = true;
    public List<ToolbarClockSetting> ToolbarClocks { get; set; } = [];
    public bool KeepWindowOnTop { get; set; }
    public bool ShowSystemStatus { get; set; } = true;
    public bool ShowChannels { get; set; } = true;
    public bool ShowAlertTones { get; set; } = true;
    public bool LockWidgets { get; set; } = true;
    public Dictionary<string, WidgetPositionSetting> ChannelWidgetPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? UserBackgroundImage { get; set; }
    public bool TogglePttMode { get; set; }
    // Portable name of the key that activates global PTT.  The desktop host
    // maps this to its platform key enum so Core remains UI-independent.
    public string GlobalPttKey { get; set; } = "Space";
    public string ActiveSystemPttKey { get; set; } = "None";
    public bool SerialPttEnabled { get; set; }
    public bool SerialPttActiveSystemOnly { get; set; }
    public string SerialPttPortName { get; set; } = string.Empty;
    public int SerialPttBaudRate { get; set; } = 9_600;
    public List<string> ReceiveEnabledChannelKeys { get; set; } = [];
    public List<string> TransmitSelectedChannelKeys { get; set; } = [];
    public string LastDtmfDigits { get; set; } = "123";
    public double ToneFrequencyHz { get; set; } = 1000;
    public double ToneDurationSeconds { get; set; } = 1.0;
    public double QuickCallToneAFrequencyHz { get; set; } = 600;
    public double QuickCallToneBFrequencyHz { get; set; } = 1200;
    public List<DtmfPresetSetting> DtmfPresets { get; set; } = [];
    public List<TonePresetSetting> TonePresets { get; set; } = [];
    public List<AlertToneSetting> AlertTones { get; set; } = [];
    public int RecordingRetentionDays { get; set; } = 7;
    public string RecordingRootPath { get; set; } = string.Empty;
    public Dictionary<string, double> ChannelVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> ChannelStereoBalances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ChannelOutputDeviceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> WebStreamOutputDeviceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> WebStreamVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> RecordingEnabledChannelKeys { get; set; } = [];
    public Dictionary<string, List<uint>> RecordingIgnoredSubscriberIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<PatchMemberSetting>> PatchGroupMemberships { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> PatchGroupModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> PatchGroupEnabledStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool RetainPatchStateOnStartup { get; set; }
    public bool RestoreSelectedChannelsOnStartup { get; set; } = true;
    public List<string> SelectedWebStreams { get; set; } = [];
    public Dictionary<string, bool> TransmitEncryptionStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShowCallHistoryPane { get; set; } = true;
    public bool SnapCallHistoryToWindow { get; set; }
    public WindowPlacementSetting MainWindowPlacement { get; set; } = new()
    {
        Width = 1260,
        Height = 760
    };
    public WindowPlacementSetting CallHistoryWindowPlacement { get; set; } = new();
}

// JSON-backed user settings store with resilient reads and atomic replacement.
// The path is injectable so tests and packaged hosts do not depend on a
// platform-specific profile location.
public sealed class UserSettingsStore
{
    private const double PresetMaxDurationSeconds = 10.0;

    private readonly UserSettingsSerializer serializer;
    private readonly AtomicSettingsFileStore fileStore;
    private readonly SettingsProfileRepository profiles;

    public UserSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        serializer = new UserSettingsSerializer();
        fileStore = new AtomicSettingsFileStore(Path);
        profiles = new SettingsProfileRepository(ProfilesDirectoryPath);
    }

    public string Path { get; }

    public string ProfilesDirectoryPath
        => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path) ?? AppContext.BaseDirectory,
            "Profiles");

    public static string DefaultPath
    {
        get
        {
            string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory))
                baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory))
                baseDirectory = AppContext.BaseDirectory;
            return System.IO.Path.Combine(baseDirectory, "DVMProject", "dvmconsole", "UserSettings.json");
        }
    }

    public UserSettings Load()
    {
        if (!fileStore.Exists)
            return new UserSettings();

        try
        {
            UserSettings settings = serializer.Deserialize(fileStore.ReadAllText())
                ?? new UserSettings();
            int storedSchemaVersion = settings.SchemaVersion;
            if (storedSchemaVersion < 2)
                settings.HighQualityBluetoothAudioEnabled = false;
            NormalizeRxAudioProcessingOptions(settings, storedSchemaVersion < 3);
            settings.RxJitterBuffer = RxJitterBufferSetting.Normalize(settings.RxJitterBuffer);
            settings.RxJitterBuffersBySystem = NormalizeRxJitterBuffersBySystem(
                settings.RxJitterBuffersBySystem);
            settings.SchemaVersion = UserSettings.CurrentSchemaVersion;
            settings.TransmitEncryptionStates ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            settings.MainWindowPlacement = NormalizeWindowPlacement(
                settings.MainWindowPlacement,
                defaultWidth: 1260,
                defaultHeight: 760,
                minimumWidth: 880,
                minimumHeight: 560,
                maximumWidth: 3840,
                maximumHeight: 2160);
            settings.CallHistoryWindowPlacement = NormalizeWindowPlacement(settings.CallHistoryWindowPlacement);
            settings.GlobalPttKey = NormalizeGlobalPttKey(settings.GlobalPttKey);
            settings.ActiveSystemPttKey = NormalizeGlobalPttKey(settings.ActiveSystemPttKey);
            ResolveDuplicateKeyboardPttKeys(settings);
            NormalizeSerialPttSettings(settings);
            settings.UserBackgroundImage = string.IsNullOrWhiteSpace(settings.UserBackgroundImage)
                ? null
                : settings.UserBackgroundImage.Trim();
            settings.RecentCodeplugPaths = NormalizeRecentCodeplugPaths(settings.RecentCodeplugPaths);
            settings.ToolbarClocks = NormalizeToolbarClocks(settings.ToolbarClocks);
            NormalizeUiSettings(settings);
            settings.ReceiveEnabledChannelKeys = NormalizeNames(settings.ReceiveEnabledChannelKeys);
            settings.TransmitSelectedChannelKeys = NormalizeNames(settings.TransmitSelectedChannelKeys);
            settings.ChannelWidgetPositions = NormalizeWidgetPositions(settings.ChannelWidgetPositions);
            NormalizeAudioInputSettings(settings);
            settings.AudioInputPresetName = settings.AudioInputPresetName?.Trim() ?? string.Empty;
            settings.AudioInputPresets = NormalizeAudioInputPresets(settings.AudioInputPresets);
            settings.LastDtmfDigits = NormalizeDtmfDigits(settings.LastDtmfDigits);
            settings.ToneFrequencyHz = NormalizeToneFrequency(settings.ToneFrequencyHz);
            settings.ToneDurationSeconds = NormalizeToneDuration(settings.ToneDurationSeconds);
            settings.QuickCallToneAFrequencyHz = NormalizeToneFrequency(settings.QuickCallToneAFrequencyHz, 600);
            settings.QuickCallToneBFrequencyHz = NormalizeToneFrequency(settings.QuickCallToneBFrequencyHz, 1200);
            settings.DtmfPresets = NormalizeDtmfPresets(settings.DtmfPresets);
            settings.TonePresets = NormalizeTonePresets(settings.TonePresets);
            settings.AlertTones = NormalizeAlertTones(settings.AlertTones);
            settings.RecordingRetentionDays = Math.Max(0, settings.RecordingRetentionDays);
            settings.RecordingRootPath = NormalizeRecordingRootPath(settings.RecordingRootPath);
            var channelVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> entry in settings.ChannelVolumes ?? [])
            {
                string channelKey = entry.Key?.Trim() ?? string.Empty;
                if (channelKey.Length > 0)
                    channelVolumes[channelKey] = NormalizeChannelVolume(entry.Value);
            }
            settings.ChannelVolumes = channelVolumes;
            settings.ChannelStereoBalances = NormalizeChannelStereoBalances(settings.ChannelStereoBalances);
            var channelOutputDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> entry in settings.ChannelOutputDeviceIds ?? [])
            {
                string channelKey = entry.Key?.Trim() ?? string.Empty;
                string deviceId = entry.Value?.Trim() ?? string.Empty;
                if (channelKey.Length > 0 && deviceId.Length > 0)
                    channelOutputDevices[channelKey] = deviceId;
            }
            settings.ChannelOutputDeviceIds = channelOutputDevices;
            settings.WebStreamOutputDeviceIds = NormalizeChannelOutputDevices(settings.WebStreamOutputDeviceIds);
            var webStreamVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> entry in settings.WebStreamVolumes ?? [])
            {
                string streamName = entry.Key?.Trim() ?? string.Empty;
                if (streamName.Length > 0)
                    webStreamVolumes[streamName] = NormalizeChannelVolume(entry.Value);
            }
            settings.WebStreamVolumes = webStreamVolumes;
            settings.RecordingEnabledChannelKeys = NormalizeNames(settings.RecordingEnabledChannelKeys);
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
            settings.PatchGroupModes = NormalizeGroupStates(settings.PatchGroupModes);
            settings.PatchGroupEnabledStates = NormalizeGroupStates(settings.PatchGroupEnabledStates);
            settings.SelectedWebStreams = NormalizeNames(settings.SelectedWebStreams);
            return settings;
        }
        catch (JsonException)
        {
            return new UserSettings();
        }
        catch (IOException)
        {
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        NormalizeSettingsForWrite(settings);

        fileStore.WriteAllText(serializer.Serialize(settings));
    }

    public void Export(UserSettings settings, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string destination = System.IO.Path.GetFullPath(destinationPath);
        if (destination.Equals(Path, StringComparison.OrdinalIgnoreCase))
        {
            Save(settings);
            return;
        }

        Save(settings);
        fileStore.CopyTo(destination);
    }

    public SettingsImportPreview PreviewImport(string sourcePath)
    {
        string source = ResolveSettingsFilePath(sourcePath);
        UserSettings settings = ReadSettingsFile(source);
        return CreatePreview(source, settings);
    }

    public SettingsImportPreview PreviewNamedProfile(string profileName)
        => PreviewImport(GetNamedProfilePath(profileName));

    public IReadOnlyList<string> ListNamedProfiles()
        => profiles.ListNames();

    public void SaveNamedProfile(string profileName, UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string profilePath = GetNamedProfilePath(profileName);
        new UserSettingsStore(profilePath).Save(settings);
    }

    public UserSettings LoadNamedProfile(string profileName)
    {
        string path = GetNamedProfilePath(profileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("Named settings profile not found.", path);
        return new UserSettingsStore(path).Load();
    }

    public UserSettings ImportNamedProfile(
        string profileName,
        SettingsImportScope scope = SettingsImportScope.OperatorState)
        => Import(GetNamedProfilePath(profileName), scope);

    public void DeleteNamedProfile(string profileName)
        => profiles.Delete(profileName);

    public UserSettings Import(
        string sourcePath,
        SettingsImportScope scope = SettingsImportScope.All)
    {
        string source = ResolveSettingsFilePath(sourcePath);
        UserSettings imported = ReadSettingsFile(source);
        if (scope == SettingsImportScope.All)
        {
            Save(imported);
            return Load();
        }

        UserSettings current = Load();
        MergeSettings(current, imported, scope);
        Save(current);
        return Load();
    }

    private static string ResolveSettingsFilePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string source = System.IO.Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Settings file not found.", source);
        return source;
    }

    private UserSettings ReadSettingsFile(string source)
    {
        UserSettings imported;

        try
        {
            imported = serializer.Deserialize(File.ReadAllText(source))
                ?? throw new InvalidDataException("The settings file did not contain a settings object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The settings file is not valid DVM Console JSON.", exception);
        }

        NormalizeSettingsForWrite(imported);
        return imported;
    }

    private static void NormalizeSettingsForWrite(UserSettings settings)
    {
        // Schema 1 defaulted this option on, so a stored true value does not
        // prove that the operator selected it. Require a fresh opt-in after
        // migration; schema 2 true values are always an explicit selection.
        if (settings.SchemaVersion < 2)
            settings.HighQualityBluetoothAudioEnabled = false;
        NormalizeRxAudioProcessingOptions(settings, settings.SchemaVersion < 3);
        settings.RxJitterBuffer = RxJitterBufferSetting.Normalize(settings.RxJitterBuffer);
        settings.RxJitterBuffersBySystem = NormalizeRxJitterBuffersBySystem(
            settings.RxJitterBuffersBySystem);
        settings.SchemaVersion = UserSettings.CurrentSchemaVersion;
        settings.DtmfPresets = NormalizeDtmfPresets(settings.DtmfPresets);
        settings.TonePresets = NormalizeTonePresets(settings.TonePresets);
        settings.MainWindowPlacement = NormalizeWindowPlacement(
            settings.MainWindowPlacement,
            defaultWidth: 1260,
            defaultHeight: 760,
            minimumWidth: 880,
            minimumHeight: 560,
            maximumWidth: 3840,
            maximumHeight: 2160);
        settings.CallHistoryWindowPlacement = NormalizeWindowPlacement(settings.CallHistoryWindowPlacement);
        settings.ToolbarClocks = NormalizeToolbarClocks(settings.ToolbarClocks);
        NormalizeUiSettings(settings);
        NormalizeAudioInputSettings(settings);
        settings.RecentCodeplugPaths = NormalizeRecentCodeplugPaths(settings.RecentCodeplugPaths);
        settings.AudioInputPresetName = settings.AudioInputPresetName?.Trim() ?? string.Empty;
        settings.AudioInputPresets = NormalizeAudioInputPresets(settings.AudioInputPresets);
        settings.ChannelOutputDeviceIds = NormalizeChannelOutputDevices(settings.ChannelOutputDeviceIds);
        settings.ChannelStereoBalances = NormalizeChannelStereoBalances(settings.ChannelStereoBalances);
        settings.WebStreamOutputDeviceIds = NormalizeChannelOutputDevices(settings.WebStreamOutputDeviceIds);
        settings.WebStreamVolumes = NormalizeWebStreamVolumes(settings.WebStreamVolumes);
        settings.RecordingRootPath = NormalizeRecordingRootPath(settings.RecordingRootPath);
        settings.RecordingEnabledChannelKeys = NormalizeNames(settings.RecordingEnabledChannelKeys);
        settings.SelectedWebStreams = NormalizeNames(settings.SelectedWebStreams);
        settings.GlobalPttKey = NormalizeGlobalPttKey(settings.GlobalPttKey);
        settings.ActiveSystemPttKey = NormalizeGlobalPttKey(settings.ActiveSystemPttKey);
        ResolveDuplicateKeyboardPttKeys(settings);
        NormalizeSerialPttSettings(settings);
        settings.ReceiveEnabledChannelKeys = NormalizeNames(settings.ReceiveEnabledChannelKeys);
        settings.TransmitSelectedChannelKeys = NormalizeNames(settings.TransmitSelectedChannelKeys);
        settings.ChannelWidgetPositions = NormalizeWidgetPositions(settings.ChannelWidgetPositions);
    }

    private static void NormalizeRxAudioProcessingOptions(UserSettings settings, bool migrateLegacyToggle)
    {
        Dictionary<string, RxAudioProcessingModeSetting> defaults =
            RxAudioProcessingModeSetting.CreateDefaults();
        Dictionary<string, RxAudioProcessingModeSetting> configured =
            settings.RxAudioProcessingOptions ?? [];
        var normalized = new Dictionary<string, RxAudioProcessingModeSetting>(StringComparer.OrdinalIgnoreCase);

        foreach (string modeKey in RxAudioProcessingModeSetting.ModeKeys)
        {
            RxAudioProcessingModeSetting source = configured
                .FirstOrDefault(entry => entry.Key.Equals(modeKey, StringComparison.OrdinalIgnoreCase))
                .Value ?? defaults[modeKey];
            normalized[modeKey] = NormalizeRxAudioProcessingMode(source);
        }

        if (migrateLegacyToggle && settings.LegacyRxAudioProcessingEnabled == false)
        {
            foreach (RxAudioProcessingModeSetting mode in normalized.Values)
            {
                mode.HighPassFilterEnabled = false;
                mode.PeakingFilterEnabled = false;
                mode.CompressorEnabled = false;
            }
        }

        settings.RxAudioProcessingOptions = normalized;
        settings.LegacyRxAudioProcessingEnabled = null;
    }

    private static RxAudioProcessingModeSetting NormalizeRxAudioProcessingMode(
        RxAudioProcessingModeSetting? setting)
    {
        setting ??= new RxAudioProcessingModeSetting();
        return new RxAudioProcessingModeSetting
        {
            HighPassFilterEnabled = setting.HighPassFilterEnabled,
            HighPassFrequencyHz = NormalizeIncrement(
                setting.HighPassFrequencyHz, 250, 0, 500, 25),
            PeakingFilterEnabled = setting.PeakingFilterEnabled,
            PeakingFrequencyHz = NormalizeIncrement(
                setting.PeakingFrequencyHz, 2_500, 250, 3_000, 25),
            PeakingGainDb = NormalizeBounded(setting.PeakingGainDb, 3, -10, 10),
            CompressorEnabled = setting.CompressorEnabled,
            CompressorRatio = NormalizeBounded(setting.CompressorRatio, 3, 1, 10),
            CompressorThresholdDbfs = NormalizeBounded(
                setting.CompressorThresholdDbfs, -18, -40, 0),
            CompressorMakeupGainDb = NormalizeBounded(
                setting.CompressorMakeupGainDb, 3, 0, 10)
        };
    }

    private static double NormalizeIncrement(
        double value,
        double fallback,
        double minimum,
        double maximum,
        double increment)
    {
        double bounded = NormalizeBounded(value, fallback, minimum, maximum);
        double snapped = minimum + Math.Round(
            (bounded - minimum) / increment,
            MidpointRounding.AwayFromZero) * increment;
        return Math.Clamp(snapped, minimum, maximum);
    }

    public void Reset()
        => fileStore.Delete();

    private string GetNamedProfilePath(string profileName)
        => profiles.GetPath(profileName);

    private static SettingsImportPreview CreatePreview(string source, UserSettings settings)
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
            !WindowPlacementsEqual(settings.MainWindowPlacement, new WindowPlacementSetting
            {
                Width = 1260,
                Height = 760
            }) ||
            !WindowPlacementsEqual(settings.CallHistoryWindowPlacement, new WindowPlacementSetting()) ||
            settings.UserBackgroundImage is not null)
        {
            sections.Add("General");
        }

        if (!string.Equals(settings.AudioInputDeviceId, "default", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(settings.AudioOutputDeviceId, "default", StringComparison.OrdinalIgnoreCase) ||
            HasCustomRxAudioProcessingOptions(settings.RxAudioProcessingOptions) ||
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

        if (!RxJitterBufferSettingsEqual(settings.RxJitterBuffer, new RxJitterBufferSetting()) ||
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

    private static void MergeSettings(UserSettings target, UserSettings source, SettingsImportScope scope)
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
            target.MainWindowPlacement = CopyWindowPlacement(source.MainWindowPlacement);
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
                entry => NormalizeRxAudioProcessingMode(entry.Value),
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
            target.RxJitterBuffersBySystem = NormalizeRxJitterBuffersBySystem(
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
                        DestinationId = member.DestinationId
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

    private static bool HasCustomRxAudioProcessingOptions(
        IReadOnlyDictionary<string, RxAudioProcessingModeSetting>? configured)
    {
        Dictionary<string, RxAudioProcessingModeSetting> defaults =
            RxAudioProcessingModeSetting.CreateDefaults();
        if (configured is null)
            return false;

        foreach (string modeKey in RxAudioProcessingModeSetting.ModeKeys)
        {
            RxAudioProcessingModeSetting? value = configured
                .FirstOrDefault(entry => entry.Key.Equals(modeKey, StringComparison.OrdinalIgnoreCase))
                .Value;
            if (value is null || !RxAudioProcessingModesEqual(value, defaults[modeKey]))
                return true;
        }
        return false;
    }

    private static bool RxAudioProcessingModesEqual(
        RxAudioProcessingModeSetting left,
        RxAudioProcessingModeSetting right)
        => left.HighPassFilterEnabled == right.HighPassFilterEnabled &&
           left.HighPassFrequencyHz == right.HighPassFrequencyHz &&
           left.PeakingFilterEnabled == right.PeakingFilterEnabled &&
           left.PeakingFrequencyHz == right.PeakingFrequencyHz &&
           left.PeakingGainDb == right.PeakingGainDb &&
           left.CompressorEnabled == right.CompressorEnabled &&
           left.CompressorRatio == right.CompressorRatio &&
           left.CompressorThresholdDbfs == right.CompressorThresholdDbfs &&
           left.CompressorMakeupGainDb == right.CompressorMakeupGainDb;

    private static bool RxJitterBufferSettingsEqual(
        RxJitterBufferSetting left,
        RxJitterBufferSetting right)
        => left.P25Milliseconds == right.P25Milliseconds &&
           left.DmrMilliseconds == right.DmrMilliseconds &&
           left.NxdnMilliseconds == right.NxdnMilliseconds &&
           left.P25Adaptive == right.P25Adaptive &&
           left.DmrAdaptive == right.DmrAdaptive &&
           left.NxdnAdaptive == right.NxdnAdaptive;

    private static Dictionary<string, RxJitterBufferSetting> NormalizeRxJitterBuffersBySystem(
        Dictionary<string, RxJitterBufferSetting>? settings)
    {
        const int maximumSystems = 128;
        const int maximumSystemNameLength = 128;
        var normalized = new Dictionary<string, RxJitterBufferSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, RxJitterBufferSetting> entry in settings ?? [])
        {
            string systemName = entry.Key?.Trim() ?? string.Empty;
            if (systemName.Length == 0 || systemName.Length > maximumSystemNameLength || entry.Value is null)
                continue;

            normalized[systemName] = RxJitterBufferSetting.Normalize(entry.Value);
            if (normalized.Count >= maximumSystems)
                break;
        }
        return normalized;
    }

    private static Dictionary<string, WidgetPositionSetting> NormalizeWidgetPositions(
        Dictionary<string, WidgetPositionSetting>? positions)
    {
        var normalized = new Dictionary<string, WidgetPositionSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, WidgetPositionSetting> entry in positions ?? [])
        {
            string key = entry.Key?.Trim() ?? string.Empty;
            WidgetPositionSetting? position = entry.Value;
            if (key.Length == 0 || position is null ||
                !double.IsFinite(position.X) || !double.IsFinite(position.Y))
            {
                continue;
            }

            normalized[key] = new WidgetPositionSetting
            {
                X = Math.Clamp(position.X, 0, 10_000),
                Y = Math.Clamp(position.Y, 0, 10_000)
            };
        }

        return normalized;
    }

    private static Dictionary<string, bool> NormalizeGroupStates(Dictionary<string, bool>? states)
    {
        var normalized = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, bool> entry in states ?? [])
        {
            string groupName = entry.Key?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(groupName))
                normalized[groupName] = entry.Value;
        }

        return normalized;
    }

    private static List<string> NormalizeNames(IEnumerable<string>? names)
    {
        return (names ?? [])
            .Select(name => name?.Trim() ?? string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> NormalizeRecentCodeplugPaths(IEnumerable<string>? paths)
    {
        var normalized = new List<string>();
        foreach (string? value in paths ?? [])
        {
            string path = value?.Trim() ?? string.Empty;
            if (path.Length == 0)
                continue;

            try
            {
                path = System.IO.Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!normalized.Contains(path, StringComparer.OrdinalIgnoreCase))
                normalized.Add(path);
            if (normalized.Count == UserSettings.MaximumRecentCodeplugs)
                break;
        }

        return normalized;
    }

    private static string NormalizeGlobalPttKey(string? key)
    {
        string candidate = key?.Trim() ?? string.Empty;
        return candidate.Equals("None", StringComparison.OrdinalIgnoreCase) ||
               candidate.Equals("Space", StringComparison.OrdinalIgnoreCase) ||
               (candidate.Length is 2 or 3 && candidate.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(candidate[1..], out int functionKey) && functionKey is >= 1 and <= 19)
            ? candidate.ToUpperInvariant() switch
            {
                "SPACE" => "Space",
                "NONE" => "None",
                var value => value
            }
            : "None";
    }

    private static void ResolveDuplicateKeyboardPttKeys(UserSettings settings)
    {
        if (!settings.ActiveSystemPttKey.Equals("None", StringComparison.OrdinalIgnoreCase) &&
            settings.ActiveSystemPttKey.Equals(settings.GlobalPttKey, StringComparison.OrdinalIgnoreCase))
        {
            settings.ActiveSystemPttKey = "None";
        }
    }

    private static void NormalizeSerialPttSettings(UserSettings settings)
    {
        settings.SerialPttPortName = settings.SerialPttPortName?.Trim() ?? string.Empty;
        settings.SerialPttBaudRate = settings.SerialPttBaudRate is >= 300 and <= 4_000_000
            ? settings.SerialPttBaudRate
            : 9_600;
        if (settings.SerialPttPortName.Length == 0)
            settings.SerialPttEnabled = false;
    }

    private static void NormalizeAudioInputSettings(UserSettings settings)
    {
        settings.AudioInputDeviceId = string.IsNullOrWhiteSpace(settings.AudioInputDeviceId)
            ? "default"
            : settings.AudioInputDeviceId.Trim();
        settings.AudioOutputDeviceId = string.IsNullOrWhiteSpace(settings.AudioOutputDeviceId)
            ? "default"
            : settings.AudioOutputDeviceId.Trim();
        settings.AudioProcessingMode = settings.AudioProcessingMode?.Trim() switch
        {
            UserSettings.AppleVoiceProcessingMode => UserSettings.AppleVoiceProcessingMode,
            _ => UserSettings.DvmConsoleAudioProcessingMode
        };
        settings.AudioInputAgcTargetDbfs = NormalizeBounded(settings.AudioInputAgcTargetDbfs, -25.0, -40.0, -12.0);
        settings.AudioInputGain = NormalizeBounded(settings.AudioInputGain, 1.0, 0.25, 3.0);
        settings.AudioInputEqLowGainDb = NormalizeBounded(settings.AudioInputEqLowGainDb, 0, -12, 12);
        settings.AudioInputEqMidGainDb = NormalizeBounded(settings.AudioInputEqMidGainDb, 0, -12, 12);
        settings.AudioInputEqHighGainDb = NormalizeBounded(settings.AudioInputEqHighGainDb, 0, -12, 12);
    }

    private static void NormalizeUiSettings(UserSettings settings)
    {
        settings.UiFontSize = NormalizeBounded(settings.UiFontSize, 14, 11, 20);
        settings.UiScale = NormalizeBounded(settings.UiScale, 1.0, 0.75, 1.5);
    }

    private static List<AudioInputPresetSetting> NormalizeAudioInputPresets(
        IEnumerable<AudioInputPresetSetting>? presets)
    {
        return (presets ?? [])
            .Where(preset => preset is not null)
            .Select(preset => new AudioInputPresetSetting
            {
                Name = string.IsNullOrWhiteSpace(preset.Name) ? "Mic Preset" : preset.Name.Trim(),
                Gain = NormalizeBounded(preset.Gain, 1.0, 0.25, 3.0),
                LowGainDb = NormalizeBounded(preset.LowGainDb, 0, -12, 12),
                MidGainDb = NormalizeBounded(preset.MidGainDb, 0, -12, 12),
                HighGainDb = NormalizeBounded(preset.HighGainDb, 0, -12, 12)
            })
            .GroupBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double NormalizeBounded(double value, double fallback, double minimum, double maximum)
        => double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static Dictionary<string, string> NormalizeChannelOutputDevices(Dictionary<string, string>? devices)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in devices ?? [])
        {
            string channelKey = entry.Key?.Trim() ?? string.Empty;
            string deviceId = entry.Value?.Trim() ?? string.Empty;
            if (channelKey.Length > 0 && deviceId.Length > 0)
                normalized[channelKey] = deviceId;
        }

        return normalized;
    }

    private static Dictionary<string, double> NormalizeWebStreamVolumes(Dictionary<string, double>? volumes)
    {
        var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> entry in volumes ?? [])
        {
            string streamName = entry.Key?.Trim() ?? string.Empty;
            if (streamName.Length > 0)
                normalized[streamName] = NormalizeChannelVolume(entry.Value);
        }

        return normalized;
    }

    private static Dictionary<string, double> NormalizeChannelStereoBalances(
        Dictionary<string, double>? balances)
    {
        var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> entry in balances ?? [])
        {
            string channelKey = entry.Key?.Trim() ?? string.Empty;
            if (channelKey.Length > 0)
                normalized[channelKey] = NormalizeBounded(entry.Value, 0, -1, 1);
        }

        return normalized;
    }

    private static string NormalizeDtmfDigits(string? digits)
    {
        string normalized = new string((digits ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character))
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalized.Length is > 0 and <= 64 && normalized.All(character => "0123456789*#ABCD".Contains(character))
            ? normalized
            : "123";
    }

    private static double NormalizeToneFrequency(double frequency, double fallback = 1000)
        => double.IsFinite(frequency) && frequency is >= 300 and <= 2500 ? frequency : fallback;

    private static double NormalizeToneDuration(double duration)
        => double.IsFinite(duration) && duration is > 0 and <= 10 ? duration : 1.0;

    private static double NormalizeChannelVolume(double volume)
        => double.IsFinite(volume) ? Math.Clamp(volume, 0, 4) : 1.0;

    private static string NormalizeRecordingRootPath(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return string.Empty;

        try
        {
            return System.IO.Path.GetFullPath(rootPath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static List<ToolbarClockSetting> NormalizeToolbarClocks(IEnumerable<ToolbarClockSetting>? clocks)
    {
        List<ToolbarClockSetting> normalized = (clocks ?? [])
            .Take(UserSettings.MaximumToolbarClocks)
            .Select(clock => new ToolbarClockSetting
            {
                Enabled = clock?.Enabled == true,
                UtcOffsetHours = Math.Clamp(clock?.UtcOffsetHours ?? 0, -12, 14),
                ColorHex = ToolbarClockColorPalette.Normalize(clock?.ColorHex)
            })
            .ToList();
        while (normalized.Count < UserSettings.MaximumToolbarClocks)
            normalized.Add(new ToolbarClockSetting());
        return normalized;
    }

    private static WindowPlacementSetting CopyWindowPlacement(WindowPlacementSetting source)
        => new()
        {
            Left = source.Left,
            Top = source.Top,
            Width = source.Width,
            Height = source.Height
        };

    private static WindowPlacementSetting NormalizeWindowPlacement(
        WindowPlacementSetting? placement,
        double defaultWidth = 560,
        double defaultHeight = 500,
        double minimumWidth = 400,
        double minimumHeight = 300,
        double maximumWidth = 1800,
        double maximumHeight = 1400)
    {
        placement ??= new WindowPlacementSetting
        {
            Width = defaultWidth,
            Height = defaultHeight
        };
        return new WindowPlacementSetting
        {
            Left = placement.Left is double left && double.IsFinite(left) ? left : null,
            Top = placement.Top is double top && double.IsFinite(top) ? top : null,
            Width = NormalizeBounded(placement.Width, defaultWidth, minimumWidth, maximumWidth),
            Height = NormalizeBounded(placement.Height, defaultHeight, minimumHeight, maximumHeight)
        };
    }

    private static bool WindowPlacementsEqual(
        WindowPlacementSetting left,
        WindowPlacementSetting right)
        => left.Left == right.Left &&
            left.Top == right.Top &&
            left.Width == right.Width &&
            left.Height == right.Height;

    private static List<DtmfPresetSetting> NormalizeDtmfPresets(IEnumerable<DtmfPresetSetting>? presets)
    {
        return (presets ?? [])
            .Where(preset => preset is not null)
            .Select(NormalizeDtmfPreset)
            .ToList();
    }

    private static DtmfPresetSetting NormalizeDtmfPreset(DtmfPresetSetting preset)
    {
        string fallbackDigits = NormalizeDtmfDigits(preset.Digits);
        List<DtmfPresetStepSetting> steps = (preset.Steps ?? [])
            .Where(step => step is not null)
            .Select(step =>
            {
                bool isHold = string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase);
                return new DtmfPresetStepSetting
                {
                    Kind = isHold ? AudioPresetStepKinds.Hold : AudioPresetStepKinds.Digit,
                    Digit = isHold ? string.Empty : NormalizeDtmfDigit(step.Digit),
                    DurationSeconds = NormalizePresetDuration(step.DurationSeconds, 0.25)
                };
            })
            .ToList();

        if (steps.Count == 0)
        {
            steps = fallbackDigits
                .Select(digit => new DtmfPresetStepSetting
                {
                    Kind = AudioPresetStepKinds.Digit,
                    Digit = digit.ToString(),
                    DurationSeconds = 0.25
                })
                .ToList();
        }

        string stepDigits = string.Concat(steps
            .Where(step => step.Kind == AudioPresetStepKinds.Digit)
            .Select(step => step.Digit));
        return new DtmfPresetSetting
        {
            Name = string.IsNullOrWhiteSpace(preset.Name) ? "DTMF Preset" : preset.Name.Trim(),
            Digits = stepDigits.Length == 0 ? fallbackDigits : stepDigits,
            Steps = steps
        };
    }

    private static List<TonePresetSetting> NormalizeTonePresets(IEnumerable<TonePresetSetting>? presets)
    {
        return (presets ?? [])
            .Where(preset => preset is not null)
            .Select(NormalizeTonePreset)
            .ToList();
    }

    private static List<AlertToneSetting> NormalizeAlertTones(IEnumerable<AlertToneSetting>? tones)
        => (tones ?? [])
            .Where(tone => tone is not null && !string.IsNullOrWhiteSpace(tone.FilePath))
            .Select(tone => new AlertToneSetting
            {
                Name = string.IsNullOrWhiteSpace(tone.Name)
                    ? System.IO.Path.GetFileNameWithoutExtension(tone.FilePath.Trim())
                    : tone.Name.Trim(),
                FilePath = tone.FilePath.Trim()
            })
            .Where(tone => tone.FilePath.Length > 0)
            .GroupBy(tone => tone.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    private static TonePresetSetting NormalizeTonePreset(TonePresetSetting preset)
    {
        double fallbackFrequency = NormalizeToneFrequency(preset.FrequencyHz);
        double fallbackDuration = NormalizeToneDuration(preset.DurationSeconds);
        List<TonePresetStepSetting> steps = (preset.Steps ?? [])
            .Where(step => step is not null)
            .Select(step =>
            {
                bool isHold = string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase);
                return new TonePresetStepSetting
                {
                    Kind = isHold ? AudioPresetStepKinds.Hold : AudioPresetStepKinds.Tone,
                    FrequencyHz = isHold ? 0 : NormalizeToneFrequency(step.FrequencyHz),
                    DurationSeconds = NormalizePresetDuration(step.DurationSeconds, fallbackDuration)
                };
            })
            .ToList();

        if (steps.Count == 0)
        {
            steps =
            [
                new TonePresetStepSetting
                {
                    Kind = AudioPresetStepKinds.Tone,
                    FrequencyHz = fallbackFrequency,
                    DurationSeconds = fallbackDuration
                }
            ];
        }

        TonePresetStepSetting? firstTone = steps.FirstOrDefault(step => step.Kind == AudioPresetStepKinds.Tone);
        return new TonePresetSetting
        {
            Name = string.IsNullOrWhiteSpace(preset.Name) ? "Tone Preset" : preset.Name.Trim(),
            FrequencyHz = firstTone?.FrequencyHz ?? fallbackFrequency,
            DurationSeconds = firstTone?.DurationSeconds ?? fallbackDuration,
            Steps = steps
        };
    }

    private static string NormalizeDtmfDigit(string? digit)
    {
        string normalized = (digit ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == 1 && "0123456789*#ABCD".Contains(normalized[0])
            ? normalized
            : "1";
    }

    private static double NormalizePresetDuration(double duration, double fallback)
        => double.IsFinite(duration) && duration > 0
            ? Math.Min(duration, PresetMaxDurationSeconds)
            : fallback;
}
