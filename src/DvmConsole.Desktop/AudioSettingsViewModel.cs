using DvmConsole.Core.Settings;
using DvmConsole.Vocoder;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

internal sealed class AudioSettingsViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<AudioInputPresetViewModel> audioInputPresets = [];
    private readonly ObservableCollection<RxAudioProcessingModeViewModel> rxAudioProcessingModes = [];
    private readonly ObservableCollection<AudioDeviceOptionViewModel> audioInputDevices = [];
    private readonly ObservableCollection<AudioDeviceOptionViewModel> audioOutputDevices = [];
    private string audioInputDeviceIdText;
    private string audioOutputDeviceIdText;
    private string audioInputGainText;
    private string audioInputLowGainText;
    private string audioInputMidGainText;
    private string audioInputHighGainText;
    private string audioInputAgcTargetDbfsText;
    private bool audioInputAgcEnabled;
    private string selectedAudioProcessingMode;
    private string audioInputPresetNameText;
    private AudioDeviceOptionViewModel? selectedAudioInputDevice;
    private AudioDeviceOptionViewModel? selectedAudioOutputDevice;

    public AudioSettingsViewModel(UserSettings settings, string selectedAudioProcessingMode)
    {
        ArgumentNullException.ThrowIfNull(settings);
        audioInputDeviceIdText = settings.AudioInputDeviceId;
        audioOutputDeviceIdText = settings.AudioOutputDeviceId;
        audioInputGainText = settings.AudioInputGain.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputLowGainText = settings.AudioInputEqLowGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputMidGainText = settings.AudioInputEqMidGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputHighGainText = settings.AudioInputEqHighGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputAgcTargetDbfsText = settings.AudioInputAgcTargetDbfs.ToString("0.###", CultureInfo.InvariantCulture);
        audioInputAgcEnabled = settings.AudioInputAgcEnabled;
        this.selectedAudioProcessingMode = selectedAudioProcessingMode;
        audioInputPresetNameText = settings.AudioInputPresetName;

        foreach ((string key, string label, VocoderMode mode) in new[]
        {
            (RxAudioProcessingModeSetting.P25Phase1Mode, "P25 Phase 1", VocoderMode.P25Imbe),
            (RxAudioProcessingModeSetting.P25Phase2Mode, "P25 Phase 2", VocoderMode.P25Phase2Ambe),
            (RxAudioProcessingModeSetting.DmrMode, "DMR", VocoderMode.DmrAmbe),
            (RxAudioProcessingModeSetting.NxdnMode, "NXDN", VocoderMode.NxdnAmbe)
        })
        {
            rxAudioProcessingModes.Add(new RxAudioProcessingModeViewModel(
                key,
                label,
                mode,
                settings.RxAudioProcessingOptions[key]));
        }
        foreach (AudioInputPresetSetting preset in settings.AudioInputPresets)
            audioInputPresets.Add(new AudioInputPresetViewModel(preset));

        AudioInputPresets = new ReadOnlyObservableCollection<AudioInputPresetViewModel>(audioInputPresets);
        RxAudioProcessingModes = new ReadOnlyObservableCollection<RxAudioProcessingModeViewModel>(rxAudioProcessingModes);
        AudioInputDevices = new ReadOnlyObservableCollection<AudioDeviceOptionViewModel>(audioInputDevices);
        AudioOutputDevices = new ReadOnlyObservableCollection<AudioDeviceOptionViewModel>(audioOutputDevices);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ObservableCollection<AudioInputPresetViewModel> MutableAudioInputPresets => audioInputPresets;
    internal ObservableCollection<RxAudioProcessingModeViewModel> MutableRxAudioProcessingModes => rxAudioProcessingModes;
    internal ObservableCollection<AudioDeviceOptionViewModel> MutableAudioInputDevices => audioInputDevices;
    internal ObservableCollection<AudioDeviceOptionViewModel> MutableAudioOutputDevices => audioOutputDevices;

    public ReadOnlyObservableCollection<AudioInputPresetViewModel> AudioInputPresets { get; }
    public ReadOnlyObservableCollection<RxAudioProcessingModeViewModel> RxAudioProcessingModes { get; }
    public ReadOnlyObservableCollection<AudioDeviceOptionViewModel> AudioInputDevices { get; }
    public ReadOnlyObservableCollection<AudioDeviceOptionViewModel> AudioOutputDevices { get; }

    public string AudioInputDeviceIdText
    {
        get => audioInputDeviceIdText;
        set => SetField(ref audioInputDeviceIdText, value ?? string.Empty);
    }

    public string AudioOutputDeviceIdText
    {
        get => audioOutputDeviceIdText;
        set => SetField(ref audioOutputDeviceIdText, value ?? string.Empty);
    }

    public AudioDeviceOptionViewModel? SelectedAudioInputDevice
    {
        get => selectedAudioInputDevice;
        set
        {
            if (ReferenceEquals(selectedAudioInputDevice, value))
                return;
            selectedAudioInputDevice = value;
            NotifyPropertyChanged();
            if (value is not null)
                AudioInputDeviceIdText = value.Id;
        }
    }

    public AudioDeviceOptionViewModel? SelectedAudioOutputDevice
    {
        get => selectedAudioOutputDevice;
        set
        {
            if (ReferenceEquals(selectedAudioOutputDevice, value))
                return;
            selectedAudioOutputDevice = value;
            NotifyPropertyChanged();
            if (value is not null)
                AudioOutputDeviceIdText = value.Id;
        }
    }

    public string AudioInputGainText
    {
        get => audioInputGainText;
        set => SetField(ref audioInputGainText, value ?? string.Empty);
    }

    public string AudioInputLowGainText
    {
        get => audioInputLowGainText;
        set => SetField(ref audioInputLowGainText, value ?? string.Empty);
    }

    public string AudioInputMidGainText
    {
        get => audioInputMidGainText;
        set => SetField(ref audioInputMidGainText, value ?? string.Empty);
    }

    public string AudioInputHighGainText
    {
        get => audioInputHighGainText;
        set => SetField(ref audioInputHighGainText, value ?? string.Empty);
    }

    public bool AudioInputAgcEnabled
    {
        get => audioInputAgcEnabled;
        set => SetField(ref audioInputAgcEnabled, value);
    }

    public string AudioInputAgcTargetDbfsText
    {
        get => audioInputAgcTargetDbfsText;
        set => SetField(ref audioInputAgcTargetDbfsText, value ?? string.Empty);
    }

    public string SelectedAudioProcessingMode
    {
        get => selectedAudioProcessingMode;
        set => SetField(ref selectedAudioProcessingMode, value);
    }

    public string AudioInputPresetNameText
    {
        get => audioInputPresetNameText;
        set => SetField(ref audioInputPresetNameText, value ?? string.Empty);
    }

    public void SetResolvedDevices(
        AudioDeviceOptionViewModel? input,
        AudioDeviceOptionViewModel? output)
    {
        selectedAudioInputDevice = input;
        selectedAudioOutputDevice = output;
        NotifyPropertyChanged(nameof(SelectedAudioInputDevice));
        NotifyPropertyChanged(nameof(SelectedAudioOutputDevice));
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        NotifyPropertyChanged(propertyName);
    }

    private void NotifyPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
