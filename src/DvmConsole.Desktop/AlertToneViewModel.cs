using DvmConsole.Core.Settings;
using DvmConsole.Audio;

namespace DvmConsole.Desktop;

public sealed class AlertToneViewModel
{
    public AlertToneViewModel(AlertToneSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Name = setting.Name;
        FilePath = setting.FilePath;
    }

    public string Name { get; }
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public string DisplayText => $"{Name} — {FileName}";
    public bool IsAvailable => File.Exists(FilePath);

    public AlertToneSetting ToSetting()
        => new()
        {
            Name = Name,
            FilePath = FilePath
        };
}

public sealed class BuiltInAlertToneViewModel
{
    public BuiltInAlertToneViewModel(LegacyAlertTone tone)
    {
        Tone = tone;
        Name = $"ALERT {(int)tone}";
        Description = tone switch
        {
            LegacyAlertTone.Alert1 => "Generate 1 kHz for 3 sec",
            LegacyAlertTone.Alert2 => "Generate alternating 1.5 kHz / 800 Hz tones for 3.36 sec",
            LegacyAlertTone.Alert3 => "Generate eight 1 kHz pulses over 3.6 sec",
            _ => throw new ArgumentOutOfRangeException(nameof(tone))
        };
    }

    public LegacyAlertTone Tone { get; }
    public string Name { get; }
    public string Description { get; }

    public short[] GenerateSamples()
        => LegacyAlertToneGenerator.Generate(Tone);
}
