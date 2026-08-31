using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IRecorderSettingsViewModel
{
    public bool IsExternalRecordingLocationAvailable => true;

    public string RecordingLocationText
    {
        get => RecordingRootPathText;
        set => RecordingRootPathText = value;
    }

    System.Collections.IEnumerable IRecorderSettingsViewModel.RecorderSystems => Systems;
}
