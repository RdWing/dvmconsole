// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Outcome of a file playback session.
    /// </summary>
    public enum AudioPlaybackOutcome
    {
        /// <summary>Playback ran to the end of the file.</summary>
        Completed,

        /// <summary>Playback was cancelled before finishing.</summary>
        Cancelled,

        /// <summary>Playback failed; details are in <see cref="AudioPlaybackResult.ErrorMessage"/>.</summary>
        Failed,
    }

    /// <summary>
    /// Result of a file playback session: outcome plus an optional diagnostic
    /// message when playback failed.
    /// </summary>
    /// <param name="Outcome">How playback ended.</param>
    /// <param name="ErrorMessage">Diagnostic message when the outcome is Failed, otherwise null.</param>
    public readonly record struct AudioPlaybackResult(AudioPlaybackOutcome Outcome, string? ErrorMessage);

    /// <summary>
    /// Plays raw PCM audio files. Cancellation and failures are reported as typed
    /// results, never as exceptions.
    /// </summary>
    public interface IAudioFilePlayer
    {
        /// <summary>
        /// Plays a raw PCM audio file.
        /// </summary>
        /// <param name="filePath">Path of the PCM file to play.</param>
        /// <param name="cancellationToken">Cancels playback, producing a Cancelled result.</param>
        /// <returns>A task completing with the playback outcome.</returns>
        Task<AudioPlaybackResult> PlayPcmAsync(string filePath, CancellationToken cancellationToken);

        /// <summary>
        /// Stops any in-flight playback. Idempotent.
        /// </summary>
        Task StopAsync();
    }
}
