using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IToneSettingsViewModel
{
    System.Collections.IEnumerable IToneSettingsViewModel.DtmfPresets => DtmfPresets;
    System.Collections.IEnumerable IToneSettingsViewModel.ToneSequenceSteps => ToneSequenceSteps;
    System.Collections.IEnumerable IToneSettingsViewModel.TonePresets => TonePresets;
    System.Collections.IEnumerable IToneSettingsViewModel.AlertTones => AlertTones;
}
