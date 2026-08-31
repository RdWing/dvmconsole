using System.Collections;
using System.Windows.Input;

namespace DvmConsole.Presentation;

public interface IAudioDeviceOptionViewModel
{
    string DisplayName { get; }
}

public interface IRxAudioProcessingModeViewModel
{
    string ModeName { get; }
    bool HighPassFilterEnabled { get; set; }
    decimal HighPassFrequencyHz { get; set; }
    bool PeakingFilterEnabled { get; set; }
    decimal PeakingFrequencyHz { get; set; }
    decimal PeakingGainDb { get; set; }
    bool CompressorEnabled { get; set; }
    decimal CompressorRatio { get; set; }
    decimal CompressorThresholdDbfs { get; set; }
    decimal CompressorMakeupGainDb { get; set; }
}

public interface IAudioInputPresetViewModel
{
    string DisplayText { get; }
}

public interface IChannelAudioRouteViewModel
{
    string Name { get; }
    IEnumerable OutputDeviceOptions { get; }
    IAudioDeviceOptionViewModel? SelectedOutputDevice { get; set; }
    double StereoBalance { get; set; }
    string StereoBalanceText { get; }
}

public interface IChannelAudioRouteSystemViewModel
{
    string Name { get; }
    IEnumerable AudioRouteChannels { get; }
}

public interface IAudioSettingsViewModel
{
    IEnumerable AudioInputDevices { get; }
    IAudioDeviceOptionViewModel? SelectedAudioInputDevice { get; set; }
    IEnumerable AudioOutputDevices { get; }
    IAudioDeviceOptionViewModel? SelectedAudioOutputDevice { get; set; }
    ICommand RefreshAudioDevicesCommand { get; }
    bool IsMicrophonePermissionRequestAvailable { get; }
    IEnumerable RxAudioProcessingModes { get; }
    ICommand ApplyRxAudioProcessingOptionsCommand { get; }
    bool IsAppleVoiceProcessingPlatformAvailable { get; }
    IReadOnlyList<string> AudioProcessingModeOptions { get; }
    string SelectedAudioProcessingMode { get; set; }
    string AudioProcessingDescription { get; }
    bool IsDvmConsoleProcessingSelected { get; }
    string AudioInputGainText { get; set; }
    string AudioInputLowGainText { get; set; }
    string AudioInputMidGainText { get; set; }
    string AudioInputHighGainText { get; set; }
    bool AudioInputAgcEnabled { get; set; }
    bool IsAgcTargetEnabled { get; }
    string AudioInputAgcTargetDbfsText { get; set; }
    bool KeepTransmitMicrophoneWarm { get; set; }
    ICommand ApplyAudioInputSettingsCommand { get; }
    string AudioInputPresetNameText { get; set; }
    IEnumerable AudioInputPresets { get; }
    IEnumerable AudioRouteSystems { get; }
}

public sealed class AudioInputPresetEventArgs(IAudioInputPresetViewModel preset) : EventArgs
{
    public IAudioInputPresetViewModel Preset { get; } = preset ?? throw new ArgumentNullException(nameof(preset));
}

public sealed class ChannelAudioRouteEventArgs(IChannelAudioRouteViewModel channel) : EventArgs
{
    public IChannelAudioRouteViewModel Channel { get; } = channel ?? throw new ArgumentNullException(nameof(channel));
}
