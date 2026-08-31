namespace DvmConsole.Core.Settings;

internal static class UserSettingsNormalizationRules
{
    private const double PresetMaxDurationSeconds = 10.0;

    internal static void NormalizeRxAudioProcessingOptions(UserSettings settings, bool migrateLegacyToggle)
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

    internal static RxAudioProcessingModeSetting NormalizeRxAudioProcessingMode(
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

    internal static bool HasCustomRxAudioProcessingOptions(
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

    internal static bool RxJitterBufferSettingsEqual(
        RxJitterBufferSetting left,
        RxJitterBufferSetting right)
        => left.P25Milliseconds == right.P25Milliseconds &&
           left.DmrMilliseconds == right.DmrMilliseconds &&
           left.NxdnMilliseconds == right.NxdnMilliseconds &&
           left.P25Adaptive == right.P25Adaptive &&
           left.DmrAdaptive == right.DmrAdaptive &&
           left.NxdnAdaptive == right.NxdnAdaptive;

    internal static Dictionary<string, RxJitterBufferSetting> NormalizeRxJitterBuffersBySystem(
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

    internal static Dictionary<string, WidgetPositionSetting> NormalizeWidgetPositions(
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

    internal static Dictionary<string, bool> NormalizeGroupStates(Dictionary<string, bool>? states)
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

    internal static List<string> NormalizeNames(IEnumerable<string>? names)
    {
        return (names ?? [])
            .Select(name => name?.Trim() ?? string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static List<string> NormalizeRecentCodeplugPaths(IEnumerable<string>? paths)
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

    internal static string NormalizeGlobalPttKey(string? key)
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

    internal static void ResolveDuplicateKeyboardPttKeys(UserSettings settings)
    {
        if (!settings.ActiveSystemPttKey.Equals("None", StringComparison.OrdinalIgnoreCase) &&
            settings.ActiveSystemPttKey.Equals(settings.GlobalPttKey, StringComparison.OrdinalIgnoreCase))
        {
            settings.ActiveSystemPttKey = "None";
        }
    }

    internal static void NormalizeSerialPttSettings(UserSettings settings)
    {
        settings.SerialPttPortName = settings.SerialPttPortName?.Trim() ?? string.Empty;
        settings.SerialPttBaudRate = settings.SerialPttBaudRate is >= 300 and <= 4_000_000
            ? settings.SerialPttBaudRate
            : 9_600;
        if (settings.SerialPttPortName.Length == 0)
            settings.SerialPttEnabled = false;
    }

    internal static void NormalizeAudioInputSettings(UserSettings settings)
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
            UserSettings.WindowsCommunicationsProcessingMode => UserSettings.WindowsCommunicationsProcessingMode,
            _ => UserSettings.DvmConsoleAudioProcessingMode
        };
        settings.AudioInputAgcTargetDbfs = NormalizeBounded(settings.AudioInputAgcTargetDbfs, -25.0, -40.0, -12.0);
        settings.AudioInputGain = NormalizeBounded(settings.AudioInputGain, 1.0, 0.25, 3.0);
        settings.AudioInputEqLowGainDb = NormalizeBounded(settings.AudioInputEqLowGainDb, 0, -12, 12);
        settings.AudioInputEqMidGainDb = NormalizeBounded(settings.AudioInputEqMidGainDb, 0, -12, 12);
        settings.AudioInputEqHighGainDb = NormalizeBounded(settings.AudioInputEqHighGainDb, 0, -12, 12);
    }

    internal static void NormalizeUiSettings(UserSettings settings)
    {
        settings.UiFontSize = NormalizeBounded(settings.UiFontSize, 14, 11, 20);
        settings.UiScale = NormalizeBounded(settings.UiScale, 1.0, 0.75, 1.5);
    }

    internal static List<AudioInputPresetSetting> NormalizeAudioInputPresets(
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

    internal static Dictionary<string, string> NormalizeChannelOutputDevices(Dictionary<string, string>? devices)
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

    internal static Dictionary<string, double> NormalizeWebStreamVolumes(Dictionary<string, double>? volumes)
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

    internal static Dictionary<string, double> NormalizeChannelStereoBalances(
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

    internal static string NormalizeDtmfDigits(string? digits)
    {
        string normalized = new string((digits ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character))
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalized.Length is > 0 and <= 64 && normalized.All(character => "0123456789*#ABCD".Contains(character))
            ? normalized
            : "123";
    }

    internal static double NormalizeToneFrequency(double frequency, double fallback = 1000)
        => double.IsFinite(frequency) && frequency is >= 300 and <= 2500 ? frequency : fallback;

    internal static double NormalizeToneDuration(double duration)
        => double.IsFinite(duration) && duration is > 0 and <= 10 ? duration : 1.0;

    internal static double NormalizeChannelVolume(double volume)
        => double.IsFinite(volume) ? Math.Clamp(volume, 0, 4) : 1.0;

    internal static string NormalizeRecordingRootPath(string? rootPath)
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

    internal static List<ToolbarClockSetting> NormalizeToolbarClocks(IEnumerable<ToolbarClockSetting>? clocks)
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

    internal static WindowPlacementSetting CopyWindowPlacement(WindowPlacementSetting source)
        => new()
        {
            Left = source.Left,
            Top = source.Top,
            Width = source.Width,
            Height = source.Height
        };

    internal static WindowPlacementSetting NormalizeWindowPlacement(
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

    internal static bool WindowPlacementsEqual(
        WindowPlacementSetting left,
        WindowPlacementSetting right)
        => left.Left == right.Left &&
            left.Top == right.Top &&
            left.Width == right.Width &&
            left.Height == right.Height;

    internal static List<DtmfPresetSetting> NormalizeDtmfPresets(IEnumerable<DtmfPresetSetting>? presets)
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

    internal static List<TonePresetSetting> NormalizeTonePresets(IEnumerable<TonePresetSetting>? presets)
    {
        return (presets ?? [])
            .Where(preset => preset is not null)
            .Select(NormalizeTonePreset)
            .ToList();
    }

    internal static List<AlertToneSetting> NormalizeAlertTones(IEnumerable<AlertToneSetting>? tones)
        => (tones ?? [])
            .Where(tone => tone is not null && (
                Guid.TryParse(tone.AssetId, out _) ||
                !string.IsNullOrWhiteSpace(tone.FilePath)))
            .Select(tone => new AlertToneSetting
            {
                Name = string.IsNullOrWhiteSpace(tone.Name)
                    ? System.IO.Path.GetFileNameWithoutExtension(
                        !string.IsNullOrWhiteSpace(tone.FileName)
                            ? tone.FileName.Trim()
                            : tone.FilePath.Trim())
                    : tone.Name.Trim(),
                AssetId = Guid.TryParse(tone.AssetId, out Guid assetId)
                    ? assetId.ToString("N")
                    : null,
                FileName = !string.IsNullOrWhiteSpace(tone.FileName)
                    ? System.IO.Path.GetFileName(tone.FileName.Trim())
                    : System.IO.Path.GetFileName(tone.FilePath.Trim()),
                FilePath = tone.FilePath.Trim()
            })
            .GroupBy(
                tone => tone.AssetId ?? tone.FilePath,
                StringComparer.OrdinalIgnoreCase)
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
