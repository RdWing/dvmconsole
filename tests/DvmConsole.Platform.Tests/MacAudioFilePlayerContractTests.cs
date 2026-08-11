// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Gate 0.1 RED contract for raw PCM file-open failures. The internal
    /// macOS player is constructed without a factory because these paths must
    /// fail before any CoreAudio output is requested.
    /// </summary>
    public sealed class MacAudioFilePlayerContractTests
    {
        [Fact]
        public async Task MissingFile_ReturnsTypedFailure_WithoutThrowing()
        {
            string path = Path.Combine(Path.GetTempPath(), $"dvmconsole-missing-{Guid.NewGuid():N}.pcm");
            var result = await CreatePlayer().PlayPcmAsync(path, CancellationToken.None);

            Assert.Equal(AudioPlaybackOutcome.Failed, result.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public async Task UnreadableFile_ReturnsTypedFailure_WithoutThrowing()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            string path = Path.Combine(Path.GetTempPath(), $"dvmconsole-unreadable-{Guid.NewGuid():N}.pcm");
            await File.WriteAllBytesAsync(path, new byte[AudioPcm.BlockBytes]);
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.None);
                var result = await CreatePlayer().PlayPcmAsync(path, CancellationToken.None);

                Assert.Equal(AudioPlaybackOutcome.Failed, result.Outcome);
                Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
            }
            finally
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Delete(path);
            }
        }

        private static IAudioFilePlayer CreatePlayer()
        {
            Type playerType = typeof(MacAudioStreamFactory).Assembly.GetType(
                "DvmConsole.Platform.Audio.Mac.MacAudioFilePlayer",
                throwOnError: true)!;
            return (IAudioFilePlayer)Activator.CreateInstance(
                playerType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object?[] { null },
                culture: null)!;
        }
    }
}
