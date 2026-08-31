using DvmConsole.Core.Settings;
using DvmConsole.Presentation;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

public sealed class DtmfPresetViewModel : IDtmfPresetViewModel
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

public sealed class TonePresetViewModel : ITonePresetViewModel
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

public sealed class ToneSequenceStepViewModel : IToneSequenceStepViewModel, INotifyPropertyChanged
{
    private bool isSilence;
    private string frequencyText;
    private string durationText;

    public ToneSequenceStepViewModel(double frequencyHz, double durationSeconds, bool isSilence = false)
    {
        this.isSilence = isSilence;
        frequencyText = frequencyHz.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        durationText = durationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSilence
    {
        get => isSilence;
        set => SetField(ref isSilence, value);
    }

    public string FrequencyText
    {
        get => frequencyText;
        set => SetField(ref frequencyText, value ?? string.Empty);
    }

    public string DurationText
    {
        get => durationText;
        set => SetField(ref durationText, value ?? string.Empty);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
