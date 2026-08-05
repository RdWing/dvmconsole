// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Reason an audio input stream ended, surfaced as a typed value instead of an
    /// exception so callers can switch on the outcome.
    /// </summary>
    public enum AudioStreamStopReason
    {
        /// <summary>Stopped deliberately via <see cref="IAudioInput.StopAsync"/>.</summary>
        Requested,

        /// <summary>Cancelled via the <see cref="CancellationToken"/> passed to StartAsync.</summary>
        Cancelled,

        /// <summary>The underlying device was lost or unplugged.</summary>
        DeviceLost,

        /// <summary>The stream failed; details are in <see cref="AudioStreamEnd.ErrorKind"/> and
        /// <see cref="AudioStreamEnd.ErrorMessage"/>.</summary>
        Error,
    }

    /// <summary>
    /// Typed categories of audio device failures.
    /// </summary>
    public enum AudioDeviceErrorKind
    {
        /// <summary>The device is not available (missing, busy or unplugged).</summary>
        DeviceUnavailable,

        /// <summary>The device could not be opened.</summary>
        OpenFailed,

        /// <summary>Reading from the device failed.</summary>
        ReadFailed,

        /// <summary>An unspecified device failure.</summary>
        Unknown,
    }

    /// <summary>
    /// Typed end-of-stream description for audio inputs: the stop reason plus
    /// optional error details when the stream failed.
    /// </summary>
    public sealed class AudioStreamEnd
    {
        private AudioStreamEnd(
            AudioStreamStopReason stopReason,
            AudioDeviceErrorKind? errorKind,
            string? errorMessage)
        {
            StopReason = stopReason;
            ErrorKind = errorKind;
            ErrorMessage = errorMessage;
        }

        /// <summary>Why the stream ended.</summary>
        public AudioStreamStopReason StopReason { get; }

        /// <summary>Error category when <see cref="StopReason"/> is <see cref="AudioStreamStopReason.Error"/>,
        /// otherwise null.</summary>
        public AudioDeviceErrorKind? ErrorKind { get; }

        /// <summary>Diagnostic message when <see cref="StopReason"/> is <see cref="AudioStreamStopReason.Error"/>,
        /// otherwise null.</summary>
        public string? ErrorMessage { get; }

        /// <summary>A stream that was stopped deliberately.</summary>
        public static AudioStreamEnd Requested() => new(AudioStreamStopReason.Requested, null, null);

        /// <summary>A stream that was cancelled.</summary>
        public static AudioStreamEnd Cancelled() => new(AudioStreamStopReason.Cancelled, null, null);

        /// <summary>A stream that ended because the device was lost.</summary>
        public static AudioStreamEnd DeviceLost() => new(AudioStreamStopReason.DeviceLost, null, null);

        /// <summary>A stream that failed with the given error category and diagnostic message.</summary>
        public static AudioStreamEnd Error(AudioDeviceErrorKind kind, string errorMessage)
            => new(AudioStreamStopReason.Error, kind, errorMessage);
    }

    /// <summary>
    /// Exception thrown when an audio device operation fails, carrying the typed
    /// error category.
    /// </summary>
    public sealed class AudioDeviceException : Exception
    {
        /// <summary>
        /// Creates an audio device exception with the given category and message.
        /// </summary>
        /// <param name="kind">Typed error category.</param>
        /// <param name="message">Human-readable diagnostic message.</param>
        public AudioDeviceException(AudioDeviceErrorKind kind, string message)
            : base(message)
        {
            Kind = kind;
        }

        /// <summary>Typed error category.</summary>
        public AudioDeviceErrorKind Kind { get; }
    }

    /// <summary>
    /// A long-running capture stream. StartAsync pumps PCM frames to the supplied
    /// callback until stopped, cancelled, or the device is lost; the returned task
    /// completes with a typed <see cref="AudioStreamEnd"/> describing the outcome.
    /// </summary>
    public interface IAudioInput
    {
        /// <summary>The device this stream captures from.</summary>
        AudioDeviceInfo Device { get; }

        /// <summary>The PCM format of the captured frames.</summary>
        PcmFormat Format { get; }

        /// <summary>
        /// Starts the capture stream.
        /// </summary>
        /// <param name="onData">Callback receiving each frame of PCM data.</param>
        /// <param name="cancellationToken">Cancels the stream, producing a Cancelled end.</param>
        /// <returns>A task that completes with the typed end-of-stream description.</returns>
        Task<AudioStreamEnd> StartAsync(
            Func<ReadOnlyMemory<byte>, Task> onData,
            CancellationToken cancellationToken);

        /// <summary>
        /// Stops the stream, producing a Requested end. Idempotent, including
        /// before the stream has started.
        /// </summary>
        Task StopAsync();
    }

    /// <summary>
    /// Factory for typed audio streams and file players.
    /// </summary>
    public interface IAudioStreamFactory : IAsyncDisposable
    {
        /// <summary>Creates a capture stream for the given device and format.</summary>
        IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format);

        /// <summary>Creates a playback stream for the given device and format.</summary>
        IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format);

        /// <summary>Creates a player for PCM audio files.</summary>
        IAudioFilePlayer CreateFilePlayer();
    }
}
