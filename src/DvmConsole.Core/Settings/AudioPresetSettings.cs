namespace DvmConsole.Core.Settings;

public static class AudioPresetStepKinds
{
    public const string Digit = "digit";
    public const string Tone = "tone";
    public const string Hold = "hold";
}

public sealed class DtmfPresetStepSetting
{
    public string Kind { get; set; } = AudioPresetStepKinds.Digit;
    public string Digit { get; set; } = "1";
    public double DurationSeconds { get; set; } = 0.25;
}

public sealed class TonePresetStepSetting
{
    public string Kind { get; set; } = AudioPresetStepKinds.Tone;
    public double FrequencyHz { get; set; } = 1000;
    public double DurationSeconds { get; set; } = 1.0;
}

/// <summary>
/// A reusable DTMF preset stored in the operator profile. Presets intentionally
/// contain no system, channel, or credential data; the current selection is
/// resolved when the operator uses one.
/// </summary>
public sealed class DtmfPresetSetting
{
    public string Name { get; set; } = "DTMF Preset";
    /// <summary>
    /// Backward-compatible compact representation. New presets also persist
    /// their ordered <see cref="Steps"/> so hold timing is not lost.
    /// </summary>
    public string Digits { get; set; } = "1";
    public List<DtmfPresetStepSetting> Steps { get; set; } = [];
}

/// <summary>
/// A reusable generated single-frequency tone stored in the operator profile.
/// </summary>
public sealed class TonePresetSetting
{
    public string Name { get; set; } = "Tone Preset";
    public double FrequencyHz { get; set; } = 1000;
    public double DurationSeconds { get; set; } = 1.0;
    public List<TonePresetStepSetting> Steps { get; set; } = [];
}
