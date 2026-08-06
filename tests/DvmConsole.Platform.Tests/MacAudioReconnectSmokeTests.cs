// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Real Apple-host smoke check for the supported reopen-after-stop recovery
    /// path: after an output is stopped, resolving the default output again
    /// through a fresh catalog snapshot and opening a second output must succeed
    /// and accept PCM. This exercises the recovery path applications use when an
    /// output is reopened after a stop — it does not claim physical device
    /// unplug simulation. Intentionally a no-op on non-macOS hosts; Linux proves
    /// only compilation, never native CoreAudio behavior.
    /// </summary>
    public sealed class MacAudioReconnectSmokeTests
    {
        [Fact]
        public async Task MacAudio_DefaultOutputReopensAfterStopAndAcceptsPcm()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            await using var catalog = new MacAudioDeviceCatalog();
            await using var factory = new MacAudioStreamFactory(catalog);

            var first = factory.CreateOutput(AudioDeviceId.Default, AudioPcm.Console);
            var firstWrite = first.Write(new byte[AudioPcm.BlockBytes]);
            Assert.Equal(AudioWriteStatus.Accepted, firstWrite.Status);
            await first.StopAsync();

            // Re-resolve the default output through a fresh catalog snapshot,
            // exactly as the application recovery path does.
            var resolved = catalog.GetDefaultOutput();
            Assert.NotNull(resolved);

            var reopened = factory.CreateOutput(AudioDeviceId.Default, AudioPcm.Console);
            var reopenedWrite = reopened.Write(new byte[AudioPcm.BlockBytes]);
            Assert.Equal(AudioWriteStatus.Accepted, reopenedWrite.Status);
            await reopened.StopAsync();
        }
    }
}
