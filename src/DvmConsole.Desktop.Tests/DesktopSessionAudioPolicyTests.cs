// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DesktopSessionAudioPolicyTests
{
    [Theory]
    [InlineData(true, AudioProcessingMode.WindowsCommunications)]
    [InlineData(false, AudioProcessingMode.DvmConsole)]
    public void WindowsProcessingCannotLeakIntoOtherHosts(bool windows, AudioProcessingMode expected)
        => Assert.Equal(expected, DesktopSessionAudioPolicy.ResolveProcessingMode(UserSettings.WindowsCommunicationsProcessingMode, windows));

    [Fact]
    public void PolicyReadsCurrentRoutesAndKeepsInputProcessingSeparate()
    {
        var settings = new UserSettings { AudioOutputDeviceId = "speaker", AudioInputDeviceId = "mic", AudioInputGain = 2 };
        var policy = new DesktopSessionAudioPolicy(settings);
        Assert.Equal("speaker", policy.OutputDevice("channel"));
        settings.ChannelOutputDeviceIds["channel"] = "headphones";
        Assert.Equal("headphones", policy.OutputDevice("channel"));
        settings.AudioOutputDeviceId = "new default";
        Assert.Equal("new default", policy.OutputDevice("other"));
        Assert.Equal("mic", policy.Input.DeviceId);
        Assert.Equal(2, policy.Input.Gain);
        Assert.Equal("new default", policy.BackendConfiguration.OutputDeviceId);
        string settingsDirectory = Path.Combine(Path.GetTempPath(), "settings");
        Assert.Equal(Path.Combine(settingsDirectory, "Recordings"),
            policy.RecordingRoot(Path.Combine(settingsDirectory, "UserSettings.json")));
    }
}
