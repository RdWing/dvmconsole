namespace DvmConsole.Core.Settings;

/// <summary>
/// Persisted microphone gain and EQ preset. AGC remains an independent global
/// toggle, matching the legacy audio settings behavior.
/// </summary>
public sealed class AudioInputPresetSetting
{
    public string Name { get; set; } = string.Empty;
    public double Gain { get; set; } = 1.0;
    public double LowGainDb { get; set; }
    public double MidGainDb { get; set; }
    public double HighGainDb { get; set; }
}
