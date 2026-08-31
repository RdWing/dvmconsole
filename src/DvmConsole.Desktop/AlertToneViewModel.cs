using DvmConsole.Core.Settings;
using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed class AlertToneViewModel : IAlertToneViewModel
{
    public AlertToneViewModel(AlertToneSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Name = setting.Name;
        AssetId = setting.AssetId;
        FileName = string.IsNullOrWhiteSpace(setting.FileName)
            ? Path.GetFileName(setting.FilePath)
            : Path.GetFileName(setting.FileName);
        FilePath = setting.FilePath;
    }

    public string Name { get; }
    public string? AssetId { get; private set; }
    public string FilePath { get; private set; }
    public string FileName { get; }
    public string DisplayText => $"{Name} — {FileName}";
    public string StorageText => Guid.TryParse(AssetId, out _)
        ? $"Managed asset · {FileName}"
        : FilePath;
    public bool IsAvailable => Guid.TryParse(AssetId, out _) || File.Exists(FilePath);

    internal void SetManagedAsset(AssetId id)
    {
        AssetId = id.ToString();
        FilePath = string.Empty;
    }

    public AlertToneSetting ToSetting()
        => new()
        {
            Name = Name,
            AssetId = AssetId,
            FileName = FileName,
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

    public GeneratedToneSequence CreateSequence()
        => LegacyAlertToneGenerator.CreateSequence(Tone);
}
