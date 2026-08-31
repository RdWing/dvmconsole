using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IGeneralSettingsViewModel
{
    System.Collections.IEnumerable IGeneralSettingsViewModel.ToolbarClocks => ToolbarClocks;
}
