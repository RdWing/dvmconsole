using DvmConsole.Audio;
using DvmConsole.Ptt;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

internal sealed class PttSettingsViewModel : INotifyPropertyChanged
{
    private static readonly KeyboardPttKey[] GlobalPttKeyOptionValues =
        Enum.GetValues<KeyboardPttKey>();
    private static readonly int[] SerialPttBaudRateOptions =
        [1_200, 2_400, 4_800, 9_600, 19_200, 38_400, 57_600, 115_200];
    private readonly ObservableCollection<string> serialPttPortOptions = [];
    private KeyboardPttKey selectedGlobalPttKey;
    private KeyboardPttKey selectedActiveSystemPttKey;
    private bool togglePttMode;
    private bool serialPttEnabled;
    private bool serialPttActiveSystemOnly;
    private string serialPttPortName;
    private int serialPttBaudRate;
    private string serialPttStatusText = "Serial PTT is disabled.";

    public PttSettingsViewModel(
        KeyboardPttKey globalPttKey,
        KeyboardPttKey activeSystemPttKey,
        bool togglePttMode,
        bool serialPttEnabled,
        bool serialPttActiveSystemOnly,
        string serialPttPortName,
        int serialPttBaudRate)
    {
        selectedGlobalPttKey = globalPttKey;
        selectedActiveSystemPttKey = activeSystemPttKey;
        this.togglePttMode = togglePttMode;
        this.serialPttEnabled = serialPttEnabled;
        this.serialPttActiveSystemOnly = serialPttActiveSystemOnly;
        this.serialPttPortName = serialPttPortName;
        this.serialPttBaudRate = serialPttBaudRate;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool TogglePttMode
    {
        get => togglePttMode;
        set => SetField(ref togglePttMode, value);
    }

    public IReadOnlyList<KeyboardPttKey> GlobalPttKeyOptions => GlobalPttKeyOptionValues;

    public KeyboardPttKey SelectedGlobalPttKey
    {
        get => selectedGlobalPttKey;
        set => SetField(ref selectedGlobalPttKey, value);
    }

    public KeyboardPttKey SelectedActiveSystemPttKey
    {
        get => selectedActiveSystemPttKey;
        set => SetField(ref selectedActiveSystemPttKey, value);
    }

    public bool SerialPttEnabled
    {
        get => serialPttEnabled;
        set => SetField(ref serialPttEnabled, value);
    }

    public bool SerialPttActiveSystemOnly
    {
        get => serialPttActiveSystemOnly;
        set => SetField(ref serialPttActiveSystemOnly, value);
    }

    public string SerialPttPortName
    {
        get => serialPttPortName;
        set => SetField(ref serialPttPortName, value?.Trim() ?? string.Empty);
    }

    public int SerialPttBaudRate
    {
        get => serialPttBaudRate;
        set => SetField(ref serialPttBaudRate, value);
    }

    public IReadOnlyList<string> SerialPttPortOptions => serialPttPortOptions;

    public IReadOnlyList<int> SerialPttBaudRates
        => SerialPttBaudRateOptions
            .Append(SerialPttBaudRate)
            .Where(baudRate => baudRate > 0)
            .Distinct()
            .Order()
            .ToArray();

    public string SerialPttStatusText
    {
        get => serialPttStatusText;
        set => SetField(ref serialPttStatusText, value);
    }

    public void ReplaceSerialPttPortOptions(IEnumerable<string> portNames)
    {
        serialPttPortOptions.Clear();
        foreach (string portName in portNames)
            serialPttPortOptions.Add(portName);
    }

    public void NotifySerialPttPortOptionsChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SerialPttPortOptions)));
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
