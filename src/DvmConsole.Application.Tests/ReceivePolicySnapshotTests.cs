// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ReceivePolicySnapshotTests
{
    [Theory]
    [InlineData("Test")]
    [InlineData(" Test ")]
    public void InvalidBufferReplacementKeepsTheAppliedSnapshotAndProfiles(string systemName)
    {
        var runtime = new ReceiveBufferingRuntime();
        var option = new ConsoleReceiveBufferingOptions(540, false, 240, false, 320, false);
        var source = new Dictionary<string, ConsoleReceiveBufferingOptions> { [systemName] = option };
        runtime.Apply(source);
        source.Clear();
        var established = runtime.GetProfile(" TEST ", RadioMediaProtocol.P25);
        Assert.Equal(TimeSpan.FromMilliseconds(540), established.TargetDelay);
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.Apply(
            new Dictionary<string, ConsoleReceiveBufferingOptions> { ["Test"] = option with { P25Milliseconds = -1 } }));
        Assert.Equal(option, runtime.GetOptions("test"));
        runtime.Apply(new Dictionary<string, ConsoleReceiveBufferingOptions>
        { ["Test"] = option with { P25Milliseconds = 0 } });
        Assert.Equal(TimeSpan.Zero, runtime.GetProfile("Test", RadioMediaProtocol.P25).TargetDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(540), established.TargetDelay);
    }

    [Fact]
    public void ProcessingSnapshotIsIndependentOfMutableSavedSettingsAndSuppliesMissingModes()
    {
        var setting = new RxAudioProcessingModeSetting { HighPassFilterEnabled = true, HighPassFrequencyHz = 400 };
        var settings = new Dictionary<string, RxAudioProcessingModeSetting>
        { [RxAudioProcessingModeSetting.P25Phase1Mode] = setting };
        var snapshot = ConsoleReceiveProcessingProfile.Capture(settings);
        setting.HighPassFrequencyHz = 600;
        settings.Clear();
        Assert.Equal(4, snapshot.Count);
        Assert.True(snapshot[VocoderMode.P25Imbe].HighPassFilterEnabled);
        Assert.Equal(400, snapshot[VocoderMode.P25Imbe].HighPassFrequencyHz);
        Assert.Equal(ConsoleReceiveProcessingProfile.FromSetting(new RxAudioProcessingModeSetting()), snapshot[VocoderMode.DmrAmbe]);
    }
}
