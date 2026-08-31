using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IWebStreamsSettingsViewModel
{
    System.Collections.IEnumerable IWebStreamsSettingsViewModel.WebStreams => WebStreams;
}
