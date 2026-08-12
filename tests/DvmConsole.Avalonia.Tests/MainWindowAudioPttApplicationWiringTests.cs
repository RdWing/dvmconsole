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
            Assert.Contains("audioViewModel.Preferences?.MuteRxAudioWhileTransmitting == true", source);
            Assert.Contains("audioViewModel.Ptt?.IsEngaged == true", source);
            Assert.Contains("router.ClearAllTalkgroupBuffers();", source);
            Assert.Contains("TonePcmGenerator.GenerateTalkPermitTone()", source);
            Assert.Contains("router.PlayLocalPcmAsync", source);
            Assert.Contains("await router.BeginTransmitAsync", source);
        }

        private static string SourcePath()
            => Path.Combine(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")),
                "DvmConsole.Avalonia",
                "MainWindow.axaml.cs");
    }
}
