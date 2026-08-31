using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IGroupSettingsViewModel
{
    System.Collections.IEnumerable IGroupSettingsViewModel.PatchGroups => PatchGroups;
}
