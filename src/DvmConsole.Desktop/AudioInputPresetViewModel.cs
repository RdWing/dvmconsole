using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

public sealed class AudioInputPresetViewModel : DvmConsole.Presentation.IAudioInputPresetViewModel
{
    public AudioInputPresetViewModel(AudioInputPresetSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Name = setting.Name.Trim();
        Gain = setting.Gain;
        LowGainDb = setting.LowGainDb;
        MidGainDb = setting.MidGainDb;
        HighGainDb = setting.HighGainDb;
    }

    public string Name { get; }
    public double Gain { get; }
    public double LowGainDb { get; }
    public double MidGainDb { get; }
    public double HighGainDb { get; }
    public string DisplayText =>
        $"{Name}: gain {Gain:0.##}, EQ {LowGainDb:+0.##;-0.##;0}/{MidGainDb:+0.##;-0.##;0}/{HighGainDb:+0.##;-0.##;0} dB";

    public AudioInputPresetSetting ToSetting()
        => new()
        {
            Name = Name,
            Gain = Gain,
            LowGainDb = LowGainDb,
            MidGainDb = MidGainDb,
            HighGainDb = HighGainDb
        };
}
