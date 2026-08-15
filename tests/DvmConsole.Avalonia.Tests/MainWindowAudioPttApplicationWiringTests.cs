// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED source pins for Gate 3.3's shell application boundary.
    /// </summary>
    public sealed class MainWindowAudioPttApplicationWiringTests
    {
        [Fact]
        public void MainWindowWiresPreferencesToSpeakerOnlyMuteAndLocalPermitTone()
        {
            var source = File.ReadAllText(SourcePath());

            Assert.Contains("resolveSpeakerOutputEnabled:", source);
            Assert.Contains("private bool ShouldMuteRxPlayback()", source);
            Assert.Contains("viewModel.Preferences?.MuteRxAudioWhileTransmitting != true", source);
            Assert.Contains("viewModel.Ptt?.IsEngaged == true", source);
            Assert.Contains("Volatile.Read(ref dashboardTransmitActive) != 0", source);
            Assert.Contains("patchPttRuntimeCoordinator?.IsTransmitActive == true", source);
            Assert.Contains("router.ClearAllTalkgroupBuffers();", source);
            Assert.Contains("TonePcmGenerator.GenerateTalkPermitTone()", source);
            Assert.Contains("router.PlayLocalPcmAsync", source);
            Assert.Contains("await router.BeginTransmitAsync", source);
            Assert.Contains("PTT unavailable: select a primary channel or enable All Channels.", source);
            Assert.Contains("PTT unavailable: the primary channel is receive-only.", source);
            Assert.Contains("PTT unavailable: select a transmit-capable channel.", source);
            Assert.Contains("PTT audio start failed: check the selected input device.", source);
            Assert.Contains("Channel PTT failed: check the selected input device.", source);
            Assert.Contains("talkgroupAudioRouter.MonitorUnavailable += OnMonitorUnavailable;", source);
            Assert.Contains("Monitor audio unavailable", source);
            Assert.Contains("private void PttButton_Click", source);
            Assert.Contains("private async Task ChannelPttButton_ClickAsync", source);
            Assert.DoesNotContain("HandlePointerDownAsync(slot).ConfigureAwait(false)", source);
            Assert.DoesNotContain("HandlePointerUpAsync().ConfigureAwait(false)", source);
        }

        [Fact]
        public void MainWindowXamlRendersAudioPttStatus()
        {
            var xaml = File.ReadAllText(
                Path.Combine(
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")),
                    "DvmConsole.Avalonia",
                    "MainWindow.axaml"));

            Assert.Contains("Text=\"{Binding AudioStatusMessage}\"", xaml);
            Assert.Contains("StringConverters.IsNotNullOrEmpty", xaml);
            Assert.Contains("Click=\"PttButton_Click\"", xaml);
            Assert.Contains("Click=\"ChannelPttButton_Click\"", xaml);
        }

        private static string SourcePath()
            => Path.Combine(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")),
                "DvmConsole.Avalonia",
                "MainWindow.axaml.cs");
    }
}
