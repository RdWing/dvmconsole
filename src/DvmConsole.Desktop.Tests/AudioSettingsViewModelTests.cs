using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AudioSettingsViewModelTests
{
    [Fact]
    public void DeviceRefreshDoesNotRewriteConfiguredDeviceIds()
    {
        var settings = new UserSettings
        {
            AudioInputDeviceId = "fixed-input",
            AudioOutputDeviceId = "fixed-output"
        };
        var viewModel = new AudioSettingsViewModel(settings, "DVM Console processing");
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.SetResolvedDevices(
            new AudioDeviceOptionViewModel("resolved-input", "Input", false),
            new AudioDeviceOptionViewModel("resolved-output", "Output", false));

        Assert.Equal("fixed-input", viewModel.AudioInputDeviceIdText);
        Assert.Equal("fixed-output", viewModel.AudioOutputDeviceIdText);
        Assert.Equal(
            [
                nameof(AudioSettingsViewModel.SelectedAudioInputDevice),
                nameof(AudioSettingsViewModel.SelectedAudioOutputDevice)
            ],
            changed);
    }

    [Fact]
    public void OperatorDeviceSelectionUpdatesBoundIdAfterSelectionNotification()
    {
        var viewModel = new AudioSettingsViewModel(
            new UserSettings(),
            "DVM Console processing");
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.SelectedAudioInputDevice =
            new AudioDeviceOptionViewModel("operator-input", "Input", false);

        Assert.Equal("operator-input", viewModel.AudioInputDeviceIdText);
        Assert.Equal(
            [
                nameof(AudioSettingsViewModel.SelectedAudioInputDevice),
                nameof(AudioSettingsViewModel.AudioInputDeviceIdText)
            ],
            changed);
    }
}
