using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IAudioSettingsViewModel
{
    System.Collections.IEnumerable IAudioSettingsViewModel.AudioInputDevices => AudioInputDevices;
    IAudioDeviceOptionViewModel? IAudioSettingsViewModel.SelectedAudioInputDevice
    {
        get => SelectedAudioInputDevice;
        set => SelectedAudioInputDevice = value as AudioDeviceOptionViewModel;
    }
    System.Collections.IEnumerable IAudioSettingsViewModel.AudioOutputDevices => AudioOutputDevices;
    IAudioDeviceOptionViewModel? IAudioSettingsViewModel.SelectedAudioOutputDevice
    {
        get => SelectedAudioOutputDevice;
        set => SelectedAudioOutputDevice = value as AudioDeviceOptionViewModel;
    }
    bool IAudioSettingsViewModel.IsMicrophonePermissionRequestAvailable
        => IsMacOsPermissionRequestAvailable;
    System.Collections.IEnumerable IAudioSettingsViewModel.RxAudioProcessingModes
        => RxAudioProcessingModes;
    System.Collections.IEnumerable IAudioSettingsViewModel.AudioInputPresets
        => AudioInputPresets;
    System.Collections.IEnumerable IAudioSettingsViewModel.AudioRouteSystems
        => Systems;
}
