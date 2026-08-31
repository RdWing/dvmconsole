using DvmConsole.Presentation;
using DvmConsole.Ptt;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IPttSettingsViewModel
{
    public bool HasHardwarePttCapabilities => true;
    public bool IsKeyboardPttCapabilityAvailable => true;
    public bool IsKeyboardPermissionRequestAvailable => IsMacOsPermissionRequestAvailable;
    public bool IsSerialPttCapabilityAvailable => true;

    public string SelectedGlobalPttKeyName
    {
        get => SelectedGlobalPttKey.ToString();
        set
        {
            if (Enum.TryParse(value, ignoreCase: true, out KeyboardPttKey key))
                SelectedGlobalPttKey = key;
        }
    }

    public string SelectedActiveSystemPttKeyName
    {
        get => SelectedActiveSystemPttKey.ToString();
        set
        {
            if (Enum.TryParse(value, ignoreCase: true, out KeyboardPttKey key))
                SelectedActiveSystemPttKey = key;
        }
    }

    System.Collections.IEnumerable IPttSettingsViewModel.KeyboardPttKeyOptions
        => GlobalPttKeyOptions.Select(key => key.ToString()).ToArray();
    System.Collections.IEnumerable IPttSettingsViewModel.SerialPttPortOptions
        => SerialPttPortOptions;
    System.Collections.IEnumerable IPttSettingsViewModel.SerialPttBaudRates
        => SerialPttBaudRates;
}
