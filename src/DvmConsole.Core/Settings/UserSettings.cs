using System.Text.Json;
using System.Text.Json.Serialization;

namespace DvmConsole.Core.Settings;

/// <summary>
/// Portable placement for a modeless operator window. Coordinates are
/// optional because a display topology can change between launches.
/// </summary>
public sealed class WindowPlacementSetting
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 560;
    public double Height { get; set; } = 500;
}

/// <summary>
/// Small, portable subset of operator state that is safe to persist outside a
/// codeplug. Protocol credentials and encryption keys remain codeplug-owned.
/// </summary>
public sealed class UserSettings
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumToolbarClocks = 8;
    public const int MaximumRecentCodeplugs = 8;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string? LastCodeplugPath { get; set; }
    public List<string> RecentCodeplugPaths { get; set; } = [];
    public string? LastSelectedSystemName { get; set; }
    public string? LastSelectedChannelKey { get; set; }
    public string AudioInputDeviceId { get; set; } = "default";
    public string AudioOutputDeviceId { get; set; } = "default";
    public bool AudioInputAgcEnabled { get; set; }
    public double AudioInputGain { get; set; } = 1.0;
    public double AudioInputEqLowGainDb { get; set; }
    public double AudioInputEqMidGainDb { get; set; }
    public double AudioInputEqHighGainDb { get; set; }
    public string AudioInputPresetName { get; set; } = string.Empty;
    public List<AudioInputPresetSetting> AudioInputPresets { get; set; } = [];
    public bool MuteRxAudioWhileTransmitting { get; set; } = true;
    public bool TalkPermitTone { get; set; }
    public bool ConnectionChimes { get; set; }
    public bool DarkMode { get; set; }
    public bool ClockUse24HourTime { get; set; } = true;
    public bool ClockShowSeconds { get; set; } = true;
    public List<ToolbarClockSetting> ToolbarClocks { get; set; } = [];
    public bool KeepWindowOnTop { get; set; }
    public bool ShowSystemStatus { get; set; } = true;
    public bool ShowChannels { get; set; } = true;
    public bool ShowAlertTones { get; set; } = true;
    public bool LockWidgets { get; set; } = true;
    public string? UserBackgroundImage { get; set; }
    public bool TogglePttMode { get; set; }
    /// <summary>
    /// Portable name of the key that activates global PTT.  The desktop host
    /// maps this to its platform key enum so Core remains UI-independent.
    /// </summary>
    public string GlobalPttKey { get; set; } = "Space";
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
    public Dictionary<string, double> ChannelVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ChannelOutputDeviceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> WebStreamOutputDeviceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> WebStreamVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<uint>> RecordingIgnoredSubscriberIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<PatchMemberSetting>> PatchGroupMemberships { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> PatchGroupModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> PatchGroupEnabledStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool RetainPatchStateOnStartup { get; set; }
    public bool RestoreSelectedChannelsOnStartup { get; set; } = true;
    public List<string> SelectedWebStreams { get; set; } = [];
    public Dictionary<string, bool> TransmitEncryptionStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShowCallHistoryPane { get; set; } = true;
    public WindowPlacementSetting CallHistoryWindowPlacement { get; set; } = new();
}

/// <summary>
/// JSON-backed user settings store with resilient reads and atomic replacement.
/// The path is injectable so tests and packaged hosts do not depend on a
/// platform-specific profile location.
/// </summary>
public sealed class UserSettingsStore
{
    private const double PresetMinDurationSeconds = 0.25;
    private const double PresetMaxDurationSeconds = 10.0;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public UserSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
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
        if (!File.Exists(Path))
            return new UserSettings();

        try
        {
            UserSettings settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(Path), SerializerOptions)
                ?? new UserSettings();
            settings.TransmitEncryptionStates ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            settings.CallHistoryWindowPlacement = NormalizeWindowPlacement(settings.CallHistoryWindowPlacement);
            settings.GlobalPttKey = NormalizeGlobalPttKey(settings.GlobalPttKey);
            settings.UserBackgroundImage = string.IsNullOrWhiteSpace(settings.UserBackgroundImage)
                ? null
                : settings.UserBackgroundImage.Trim();
            settings.RecentCodeplugPaths = NormalizeRecentCodeplugPaths(settings.RecentCodeplugPaths);
            settings.ToolbarClocks = NormalizeToolbarClocks(settings.ToolbarClocks);
            settings.TransmitSelectedChannelKeys = NormalizeNames(settings.TransmitSelectedChannelKeys);
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
            var channelVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> entry in settings.ChannelVolumes ?? [])
            {
                string channelKey = entry.Key?.Trim() ?? string.Empty;
                if (channelKey.Length > 0)
                    channelVolumes[channelKey] = NormalizeChannelVolume(entry.Value);
            }
            settings.ChannelVolumes = channelVolumes;
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

        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = $"{Path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
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
        string? directory = System.IO.Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.Copy(Path, destination, overwrite: true);
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
    {
        if (!Directory.Exists(ProfilesDirectoryPath))
            return [];

        return Directory.EnumerateFiles(ProfilesDirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .Select(file => System.IO.Path.GetFileNameWithoutExtension(file))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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
    {
        string profilePath = GetNamedProfilePath(profileName);
        if (File.Exists(profilePath))
            File.Delete(profilePath);
    }

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

    private static UserSettings ReadSettingsFile(string source)
    {
        UserSettings imported;

        try
        {
            imported = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(source), SerializerOptions)
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
        if (settings.SchemaVersion <= 0)
            settings.SchemaVersion = UserSettings.CurrentSchemaVersion;
        settings.DtmfPresets = NormalizeDtmfPresets(settings.DtmfPresets);
        settings.TonePresets = NormalizeTonePresets(settings.TonePresets);
        settings.CallHistoryWindowPlacement = NormalizeWindowPlacement(settings.CallHistoryWindowPlacement);
        NormalizeAudioInputSettings(settings);
        settings.RecentCodeplugPaths = NormalizeRecentCodeplugPaths(settings.RecentCodeplugPaths);
        settings.AudioInputPresetName = settings.AudioInputPresetName?.Trim() ?? string.Empty;
        settings.AudioInputPresets = NormalizeAudioInputPresets(settings.AudioInputPresets);
        settings.ChannelOutputDeviceIds = NormalizeChannelOutputDevices(settings.ChannelOutputDeviceIds);
        settings.WebStreamOutputDeviceIds = NormalizeChannelOutputDevices(settings.WebStreamOutputDeviceIds);
        settings.WebStreamVolumes = NormalizeWebStreamVolumes(settings.WebStreamVolumes);
        settings.SelectedWebStreams = NormalizeNames(settings.SelectedWebStreams);
        settings.GlobalPttKey = NormalizeGlobalPttKey(settings.GlobalPttKey);
        settings.TransmitSelectedChannelKeys = NormalizeNames(settings.TransmitSelectedChannelKeys);
    }

    public void Reset()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }

    private string GetNamedProfilePath(string profileName)
    {
        string normalized = NormalizeProfileName(profileName);
        Directory.CreateDirectory(ProfilesDirectoryPath);
        return System.IO.Path.Combine(ProfilesDirectoryPath, $"{normalized}.json");
    }

    private static string NormalizeProfileName(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        string normalized = profileName.Trim();
        if (normalized is "." or ".." ||
            normalized.Length > 64 ||
            normalized.Any(char.IsControl) ||
            normalized.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Contains(':') ||
            normalized.Contains(System.IO.Path.DirectorySeparatorChar) ||
            normalized.Contains(System.IO.Path.AltDirectorySeparatorChar) ||
            normalized.EndsWith('.') ||
            normalized.EndsWith(' ') ||
            IsReservedWindowsProfileName(normalized))
        {
            throw new ArgumentException(
                "Profile names must be 1-64 characters and cannot contain path separators or control characters.",
                nameof(profileName));
        }

        return normalized;
    }

    private static bool IsReservedWindowsProfileName(string profileName)
    {
        string stem = profileName.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 &&
             (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             stem[3] is >= '1' and <= '9');
    }

    private static SettingsImportPreview CreatePreview(string source, UserSettings settings)
    {
        var sections = new List<string>();
        if (settings.TalkPermitTone || settings.ConnectionChimes || settings.DarkMode ||
            settings.TogglePttMode || !string.Equals(settings.GlobalPttKey, "Space", StringComparison.OrdinalIgnoreCase) ||
            !settings.ShowSystemStatus || !settings.ShowChannels || !settings.ShowAlertTones ||
            !settings.LockWidgets || !settings.ShowCallHistoryPane || settings.UserBackgroundImage is not null)
        {
            sections.Add("General");
        }

        if (!string.Equals(settings.AudioInputDeviceId, "default", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(settings.AudioOutputDeviceId, "default", StringComparison.OrdinalIgnoreCase) ||
            settings.AudioInputAgcEnabled || settings.AudioInputPresets.Count > 0 ||
            settings.ChannelVolumes.Count > 0 || settings.ChannelOutputDeviceIds.Count > 0 ||
            settings.WebStreamOutputDeviceIds.Count > 0 || settings.WebStreamVolumes.Count > 0)
        {
            sections.Add("Audio");
        }

        if (settings.DtmfPresets.Count > 0 || settings.TonePresets.Count > 0 || settings.AlertTones.Count > 0 ||
            !string.Equals(settings.LastDtmfDigits, "123", StringComparison.Ordinal) ||
            settings.ToneFrequencyHz != 1000 || settings.ToneDurationSeconds != 1.0)
        {
            sections.Add("Presets");
        }

        if (settings.RecordingRetentionDays != 7 || settings.RecordingIgnoredSubscriberIds.Count > 0 ||
            settings.PatchGroupMemberships.Count > 0 || settings.PatchGroupModes.Count > 0 ||
            settings.PatchGroupEnabledStates.Count > 0 || settings.RetainPatchStateOnStartup)
        {
            sections.Add("Recording/patch");
        }

        if (!string.IsNullOrWhiteSpace(settings.LastCodeplugPath) || settings.RecentCodeplugPaths.Count > 0 ||
            !string.IsNullOrWhiteSpace(settings.LastSelectedSystemName) ||
            !string.IsNullOrWhiteSpace(settings.LastSelectedChannelKey) ||
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
            target.TalkPermitTone = source.TalkPermitTone;
            target.ConnectionChimes = source.ConnectionChimes;
            target.DarkMode = source.DarkMode;
            target.ClockUse24HourTime = source.ClockUse24HourTime;
            target.ClockShowSeconds = source.ClockShowSeconds;
            target.ToolbarClocks = source.ToolbarClocks.ToList();
            target.KeepWindowOnTop = source.KeepWindowOnTop;
            target.ShowSystemStatus = source.ShowSystemStatus;
            target.ShowChannels = source.ShowChannels;
            target.ShowAlertTones = source.ShowAlertTones;
            target.LockWidgets = source.LockWidgets;
            target.UserBackgroundImage = source.UserBackgroundImage;
            target.ShowCallHistoryPane = source.ShowCallHistoryPane;
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
            target.AudioInputAgcEnabled = source.AudioInputAgcEnabled;
            target.AudioInputGain = source.AudioInputGain;
            target.AudioInputEqLowGainDb = source.AudioInputEqLowGainDb;
            target.AudioInputEqMidGainDb = source.AudioInputEqMidGainDb;
            target.AudioInputEqHighGainDb = source.AudioInputEqHighGainDb;
            target.AudioInputPresetName = source.AudioInputPresetName;
            target.AudioInputPresets = source.AudioInputPresets.ToList();
            target.MuteRxAudioWhileTransmitting = source.MuteRxAudioWhileTransmitting;
            target.ChannelVolumes = new Dictionary<string, double>(source.ChannelVolumes, StringComparer.OrdinalIgnoreCase);
            target.ChannelOutputDeviceIds = new Dictionary<string, string>(source.ChannelOutputDeviceIds, StringComparer.OrdinalIgnoreCase);
            target.WebStreamOutputDeviceIds = new Dictionary<string, string>(source.WebStreamOutputDeviceIds, StringComparer.OrdinalIgnoreCase);
            target.WebStreamVolumes = new Dictionary<string, double>(source.WebStreamVolumes, StringComparer.OrdinalIgnoreCase);
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
            target.TransmitSelectedChannelKeys = source.TransmitSelectedChannelKeys.ToList();
            target.SelectedWebStreams = source.SelectedWebStreams.ToList();
            target.TransmitEncryptionStates = new Dictionary<string, bool>(source.TransmitEncryptionStates, StringComparer.OrdinalIgnoreCase);
        }
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
        return candidate.Equals("Space", StringComparison.OrdinalIgnoreCase) ||
               (candidate.Length is 2 or 3 && candidate.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(candidate[1..], out int functionKey) && functionKey is >= 1 and <= 12)
            ? candidate.ToUpperInvariant() switch
            {
                "SPACE" => "Space",
                var value => value
            }
            : "Space";
    }

    private static void NormalizeAudioInputSettings(UserSettings settings)
    {
        settings.AudioInputDeviceId = string.IsNullOrWhiteSpace(settings.AudioInputDeviceId)
            ? "default"
            : settings.AudioInputDeviceId.Trim();
        settings.AudioOutputDeviceId = string.IsNullOrWhiteSpace(settings.AudioOutputDeviceId)
            ? "default"
            : settings.AudioOutputDeviceId.Trim();
        settings.AudioInputGain = NormalizeBounded(settings.AudioInputGain, 1.0, 0.25, 3.0);
        settings.AudioInputEqLowGainDb = NormalizeBounded(settings.AudioInputEqLowGainDb, 0, -12, 12);
        settings.AudioInputEqMidGainDb = NormalizeBounded(settings.AudioInputEqMidGainDb, 0, -12, 12);
        settings.AudioInputEqHighGainDb = NormalizeBounded(settings.AudioInputEqHighGainDb, 0, -12, 12);
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
        => double.IsFinite(frequency) && frequency is >= 1 and < 4000 ? frequency : fallback;

    private static double NormalizeToneDuration(double duration)
        => double.IsFinite(duration) && duration is > 0 and <= 10 ? duration : 1.0;

    private static double NormalizeChannelVolume(double volume)
        => double.IsFinite(volume) ? Math.Clamp(volume, 0, 4) : 1.0;

    private static List<ToolbarClockSetting> NormalizeToolbarClocks(IEnumerable<ToolbarClockSetting>? clocks)
    {
        List<ToolbarClockSetting> normalized = (clocks ?? [])
            .Take(UserSettings.MaximumToolbarClocks)
            .Select(clock => new ToolbarClockSetting
            {
                Enabled = clock?.Enabled == true,
                UtcOffsetHours = Math.Clamp(clock?.UtcOffsetHours ?? 0, -12, 14)
            })
            .ToList();
        while (normalized.Count < UserSettings.MaximumToolbarClocks)
            normalized.Add(new ToolbarClockSetting());
        return normalized;
    }

    private static WindowPlacementSetting NormalizeWindowPlacement(WindowPlacementSetting? placement)
    {
        placement ??= new WindowPlacementSetting();
        return new WindowPlacementSetting
        {
            Left = placement.Left is double left && double.IsFinite(left) ? left : null,
            Top = placement.Top is double top && double.IsFinite(top) ? top : null,
            Width = NormalizeBounded(placement.Width, 560, 400, 1800),
            Height = NormalizeBounded(placement.Height, 500, 300, 1400)
        };
    }

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
        => double.IsFinite(duration)
            ? Math.Clamp(duration, PresetMinDurationSeconds, PresetMaxDurationSeconds)
            : fallback;
}
