
namespace DvmConsole.Desktop;

public sealed record AudioDeviceOptionViewModel(string Id, string Name, bool IsDefault) :
    DvmConsole.Presentation.IAudioDeviceOptionViewModel
{
    public string DisplayName => IsDefault ? $"{Name} (default)" : Name;
}
