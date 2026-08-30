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
    public const int CurrentSchemaVersion = 9;
    public const string DvmConsoleAudioProcessingMode = "DvmConsole";
    public const string AppleVoiceProcessingMode = "AppleVoiceProcessing";
    public const string WindowsCommunicationsProcessingMode = "WindowsCommunications";
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
    public bool LocalToneMonitorEnabled { get; set; } = true;
    public bool VerboseLoggingEnabled { get; set; }
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
    public Dictionary<string, CodeplugGroupState> CodeplugGroupStates { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CodeplugStudioState> CodeplugStudioStates { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
    public bool LegacyPatchGroupStateMigrated { get; set; }
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
