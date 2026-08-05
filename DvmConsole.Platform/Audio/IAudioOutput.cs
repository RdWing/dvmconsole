// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Outcome of a single write to an audio output.
    /// </summary>
    public enum AudioWriteStatus
    {
        /// <summary>The data was buffered for playback.</summary>
        Accepted,

        /// <summary>The output buffer is full; the write was rejected.</summary>
        BufferOverflow,

        /// <summary>The output was stopped before this write.</summary>
        NotStarted,

        /// <summary>The underlying device was lost.</summary>
        DeviceLost,
    }

    /// <summary>
    /// Result of a write to an audio output: status plus the number of bytes
    /// currently buffered.
    /// </summary>
    /// <param name="Status">Whether the write was accepted.</param>
    /// <param name="BufferedBytes">Bytes buffered after the write attempt.</param>
    public readonly record struct AudioWriteResult(AudioWriteStatus Status, int BufferedBytes);

    /// <summary>
    /// A playback stream: accepts PCM frames, exposes a clamped volume and can be
    /// stopped. Writes never throw for device state problems; they report the
    /// outcome through <see cref="AudioWriteResult"/>.
    /// </summary>
    public interface IAudioOutput
    {
        /// <summary>The device this stream plays to.</summary>
        AudioDeviceInfo Device { get; }

        /// <summary>The PCM format of the frames written to this stream.</summary>
        PcmFormat Format { get; }

        /// <summary>
        /// Playback volume, clamped to the unit interval [0, 1] on assignment.
        /// </summary>
        float Volume { get; set; }

        /// <summary>
        /// Writes PCM data to the output buffer.
        /// </summary>
        /// <param name="data">PCM frames to play.</param>
        /// <returns>Status of the write and the buffered byte count.</returns>
        AudioWriteResult Write(ReadOnlyMemory<byte> data);

        /// <summary>Discards all buffered data.</summary>
        void ClearBuffer();

        /// <summary>
        /// Stops the playback stream. Idempotent; writes after stop report
        /// <see cref="AudioWriteStatus.NotStarted"/>.
        /// </summary>
        Task StopAsync();
    }
}
