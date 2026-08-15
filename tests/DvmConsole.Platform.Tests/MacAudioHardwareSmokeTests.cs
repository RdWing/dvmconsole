// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Real Apple-host checks for the CoreAudio vertical slice. These tests are
    /// intentionally no-ops on non-macOS hosts; Linux proves only compilation and
    /// the managed contract suite, never native CoreAudio behavior.
    /// </summary>
    [Collection("MacAudioHardware")]
    public sealed class MacAudioHardwareSmokeTests
    {
        [Fact]
        public async Task MacAudio_HalEnumeratesAndPlaysPcm()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            await using var catalog = new MacAudioDeviceCatalog();
            var outputs = catalog.GetOutputs();
            Assert.NotEmpty(outputs);
            Assert.NotNull(catalog.GetDefaultOutput());

            await using var factory = new MacAudioStreamFactory(catalog);
            var output = factory.CreateOutput(AudioDeviceId.Default, AudioPcm.Console);
            var write = output.Write(new byte[AudioPcm.BlockBytes]);
            Assert.Equal(AudioWriteStatus.Accepted, write.Status);
            await Task.Delay(250);
            await output.StopAsync();
        }

        [Fact]
        public async Task MacAudio_AudioQueueCapturesPcm()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            await using var catalog = new MacAudioDeviceCatalog();
            Assert.NotEmpty(catalog.GetInputs());

            await using var factory = new MacAudioStreamFactory(catalog);
            var input = factory.CreateInput(AudioDeviceId.Default, AudioPcm.Console);
            var receivedBytes = 0;
            var runTask = input.StartAsync(
                data =>
                {
                    Interlocked.Add(ref receivedBytes, data.Length);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            await Task.Delay(500);
            await input.StopAsync();
            var end = await runTask;

            Assert.Equal(AudioStreamStopReason.Requested, end.StopReason);
            Assert.True(receivedBytes > 0, "The CoreAudio input callback produced no PCM data.");
        }

        [Fact]
        public async Task MacAudio_FilePlayerPlaysRawPcm()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            var pcmPath = Path.GetTempFileName();
            try
            {
                // Two blocks of silence: 200 ms of the locked console codec
                // (8000 Hz, 16-bit, mono) at 1600 bytes per block.
                await File.WriteAllBytesAsync(pcmPath, new byte[AudioPcm.BlockBytes * 2]);

                await using var catalog = new MacAudioDeviceCatalog();
                await using var factory = new MacAudioStreamFactory(catalog);
                var player = factory.CreateFilePlayer();
                var result = await player.PlayPcmAsync(pcmPath, CancellationToken.None);

                Assert.Equal(AudioPlaybackOutcome.Completed, result.Outcome);
            }
            finally
            {
                File.Delete(pcmPath);
            }
        }
    }
}
