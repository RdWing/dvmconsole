using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

public sealed class DtmfPresetViewModel
{
    public DtmfPresetViewModel(DtmfPresetSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Name = setting.Name;
        Digits = setting.Digits;
        Steps = setting.Steps.Count > 0
            ? setting.Steps.ToArray()
            : setting.Digits
                .Where(DvmConsole.Audio.DtmfToneGenerator.IsDigit)
                .Select(digit => new DtmfPresetStepSetting
                {
                    Digit = digit.ToString(),
                    DurationSeconds = 0.25
                })
                .ToArray();
    }

    public string Name { get; }
    public string Digits { get; }
    public IReadOnlyList<DtmfPresetStepSetting> Steps { get; }
    public string DisplayText => $"{Name}: {string.Join(" ", Steps.Select(FormatStep))}";

    private static string FormatStep(DtmfPresetStepSetting step)
        => string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase)
            ? $"hold/{step.DurationSeconds:0.###}s"
            : $"{step.Digit}/{step.DurationSeconds:0.###}s";
}

public sealed class TonePresetViewModel
{
    public TonePresetViewModel(TonePresetSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Name = setting.Name;
        FrequencyHz = setting.FrequencyHz;
        DurationSeconds = setting.DurationSeconds;
        Steps = setting.Steps.Count > 0
            ? setting.Steps.ToArray()
            :
            [
                new TonePresetStepSetting
                {
                    FrequencyHz = setting.FrequencyHz,
                    DurationSeconds = setting.DurationSeconds
                }
            ];
    }

    public string Name { get; }
    public double FrequencyHz { get; }
    public double DurationSeconds { get; }
    public IReadOnlyList<TonePresetStepSetting> Steps { get; }
    public string DisplayText => $"{Name}: {string.Join(" ", Steps.Select(FormatStep))}";

    private static string FormatStep(TonePresetStepSetting step)
        => string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase)
            ? $"hold/{step.DurationSeconds:0.###}s"
            : $"{step.FrequencyHz:0.###}Hz/{step.DurationSeconds:0.###}s";
}
