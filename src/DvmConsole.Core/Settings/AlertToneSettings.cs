namespace DvmConsole.Core.Settings;

// A user-profile alert asset. The path is intentionally local profile data;
// codeplug and protocol settings remain separate from operator media files.
public sealed class AlertToneSetting
{
    public string Name { get; set; } = "Alert tone";
    public string FilePath { get; set; } = string.Empty;
}
