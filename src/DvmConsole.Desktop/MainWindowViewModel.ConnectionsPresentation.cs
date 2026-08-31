using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IConnectionsSettingsViewModel
{
    System.Collections.IEnumerable IConnectionsSettingsViewModel.ConnectionSystems => Systems;
    System.Collections.IEnumerable IConnectionsSettingsViewModel.KeyStatusItems => KeyStatusItems;
}
