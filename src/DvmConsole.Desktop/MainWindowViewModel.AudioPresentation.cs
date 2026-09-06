// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
    string IAudioSettingsViewModel.AudioInputPresetFilterText
    {
        get => audioSettings.AudioInputPresetFilterText;
        set => audioSettings.AudioInputPresetFilterText = value;
    }
    bool IAudioSettingsViewModel.IsAudioInputPresetFilterVisible
        => audioSettings.IsAudioInputPresetFilterVisible;
    System.Collections.IEnumerable IAudioSettingsViewModel.FilteredAudioInputPresets
        => audioSettings.FilteredAudioInputPresets;
    System.Collections.IEnumerable IAudioSettingsViewModel.AudioRouteSystems
        => Systems;
}
