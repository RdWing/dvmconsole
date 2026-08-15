namespace DvmConsole.Core.Settings;

public sealed class ToolbarClockSetting
{
    public bool Enabled { get; set; }
    public int UtcOffsetHours { get; set; }
    public string ColorHex { get; set; } = ToolbarClockColorPalette.DefaultColorHex;
}
