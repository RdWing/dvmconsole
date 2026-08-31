using System.Collections;
using System.Windows.Input;

namespace DvmConsole.Presentation;

public interface IWebStreamViewModel
{
    string Name { get; }
    string StatusText { get; }
    string ToggleButtonText { get; }
    ICommand ToggleCommand { get; }
    double Volume { get; set; }
    IEnumerable OutputDeviceOptions { get; }
    IAudioDeviceOptionViewModel? SelectedOutputDevice { get; set; }
}

public interface IWebStreamsSettingsViewModel
{
    IEnumerable WebStreams { get; }
}

public sealed class WebStreamRouteSaveEventArgs(IWebStreamViewModel stream) : EventArgs
{
    public IWebStreamViewModel Stream { get; } = stream ?? throw new ArgumentNullException(nameof(stream));
}
