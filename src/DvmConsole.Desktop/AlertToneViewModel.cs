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
    }

    public LegacyAlertTone Tone { get; }
    public string Name { get; }
}
