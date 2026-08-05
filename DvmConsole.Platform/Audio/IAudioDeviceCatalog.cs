// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Immutable PCM audio format description: sample rate, bit depth and channel count.
    /// </summary>
    /// <param name="SampleRate">Samples per second per channel (Hz).</param>
    /// <param name="BitsPerSample">Bits per single sample, e.g. 16.</param>
    /// <param name="Channels">Number of interleaved channels, e.g. 1 (mono) or 2 (stereo).</param>
    public readonly record struct PcmFormat(int SampleRate, int BitsPerSample, int Channels)
    {
        /// <summary>
        /// Bytes consumed by one sample: <see cref="BitsPerSample"/> / 8.
        /// </summary>
        public int BytesPerSample => BitsPerSample / 8;

        /// <summary>
        /// Bytes per second of audio: sample rate times bytes per sample times channels.
        /// </summary>
        public int BytesPerSecond => SampleRate * BytesPerSample * Channels;
    }

    /// <summary>
    /// Locked PCM framing constants for the console codec (8000 Hz, 16-bit, mono).
    /// </summary>
    public static class AudioPcm
    {
        /// <summary>
        /// The locked console codec: 8000 Hz, 16-bit, mono.
        /// </summary>
        public static PcmFormat Console { get; } = new PcmFormat(8000, 16, 1);

        /// <summary>
        /// Size in bytes of one 20 ms voice frame: 320 bytes.
        /// </summary>
        public const int FrameBytes = 320;

        /// <summary>
        /// Size in bytes of one 100 ms block: 1600 bytes (exactly five frames).
        /// </summary>
        public const int BlockBytes = 1600;

        /// <summary>
        /// Number of frames covering <paramref name="byteCount"/> bytes, using ceiling
        /// division so a partial frame counts as one frame.
        /// </summary>
        public static int FrameCount(int byteCount) => (byteCount + FrameBytes - 1) / FrameBytes;

        /// <summary>
        /// True when <paramref name="byteCount"/> is an exact whole number of frames
        /// (including zero).
        /// </summary>
        public static bool IsFrameAligned(int byteCount) => byteCount % FrameBytes == 0;
    }

    /// <summary>
    /// Opaque identifier for an audio device. The default device is the empty id.
    /// </summary>
    /// <param name="Value">Raw device key or empty string for the default device.</param>
    /// <param name="IsDefault">True for the OS default device marker.</param>
    public readonly record struct AudioDeviceId(string Value, bool IsDefault)
    {
        /// <summary>
        /// The empty default-device marker: <see cref="Value"/> is the empty string.
        /// </summary>
        public static AudioDeviceId Default { get; } = new AudioDeviceId(string.Empty, true);

        /// <summary>
        /// True when the id carries no key (the default-device marker).
        /// </summary>
        public bool IsEmpty => string.IsNullOrEmpty(Value);

        /// <summary>
        /// Builds a non-default device id from a non-empty device key.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null or whitespace.</exception>
        public static AudioDeviceId FromKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A device key is required.", nameof(key));
            }

            return new AudioDeviceId(key, false);
        }
    }

    /// <summary>
    /// Direction of an audio device: capture (input) or playback (output).
    /// </summary>
    public enum AudioDeviceDirection
    {
        /// <summary>Capture device (microphone).</summary>
        Input,

        /// <summary>Playback device (speaker).</summary>
        Output,
    }

    /// <summary>
    /// Identity of an audio device: its id, direction and human-readable name.
    /// </summary>
    public sealed class AudioDeviceInfo
    {
        /// <summary>
        /// Creates a device identity.
        /// </summary>
        /// <param name="id">Opaque device id.</param>
        /// <param name="direction">Capture or playback direction.</param>
        /// <param name="name">Human-readable device name.</param>
        public AudioDeviceInfo(AudioDeviceId id, AudioDeviceDirection direction, string name)
        {
            Id = id;
            Direction = direction;
            Name = name;
        }

        /// <summary>Opaque device id.</summary>
        public AudioDeviceId Id { get; }

        /// <summary>Capture or playback direction.</summary>
        public AudioDeviceDirection Direction { get; }

        /// <summary>Human-readable device name.</summary>
        public string Name { get; }
    }

    /// <summary>
    /// Catalog of audio devices available on the host: enumeration of inputs and
    /// outputs, the default devices, and id-based resolution.
    /// </summary>
    public interface IAudioDeviceCatalog : IAsyncDisposable
    {
        /// <summary>All capture devices currently available.</summary>
        IReadOnlyList<AudioDeviceInfo> GetInputs();

        /// <summary>All playback devices currently available.</summary>
        IReadOnlyList<AudioDeviceInfo> GetOutputs();

        /// <summary>The default capture device, or null when none is available.</summary>
        AudioDeviceInfo? GetDefaultInput();

        /// <summary>The default playback device, or null when none is available.</summary>
        AudioDeviceInfo? GetDefaultOutput();

        /// <summary>
        /// Resolves a device by id across both directions.
        /// </summary>
        /// <param name="id">Device id to resolve.</param>
        /// <param name="device">The matching device, or null when not found.</param>
        /// <returns>True when a matching device was found.</returns>
        bool TryFind(AudioDeviceId id, out AudioDeviceInfo? device);
    }
}
