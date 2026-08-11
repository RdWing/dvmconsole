// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Plays the standard PCM WAVE files emitted by the TAR recorder.
    /// </summary>
    public interface IAudioWaveFilePlayer
    {
        /// <summary>
        /// Plays a PCM WAVE file and reports completion, cancellation, or failure
        /// without leaking provider exceptions to the desktop shell.
        /// </summary>
        Task<AudioPlaybackResult> PlayWavAsync(string filePath, CancellationToken cancellationToken);

        /// <summary>Stops any in-flight WAVE playback. Idempotent.</summary>
        Task StopAsync();
    }
}
