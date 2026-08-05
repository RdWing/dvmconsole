// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio.Mac
{
    /// <summary>
    /// Pure-managed key factory for macOS (CoreAudio) audio devices. Keys are
    /// stable, self-describing strings namespaced by direction; the default
    /// device is represented by null/whitespace and never by a synthetic key.
    /// Sentinel markers from the Windows backend are deliberately not reused.
    /// </summary>
    public static class MacAudioDeviceKey
    {
        /// <summary>
        /// Builds the stable device key for the given CoreAudio device.
        /// </summary>
        /// <param name="direction">Capture or playback direction.</param>
        /// <param name="uid">CoreAudio device UID (e.g. an engine ID).</param>
        /// <param name="name">Human-readable device name.</param>
        /// <param name="channels">Channel count of the device.</param>
        /// <returns>
        /// A key of the form <c>mac|input|&lt;uid&gt;|&lt;name&gt;|&lt;channels&gt;</c>
        /// (or <c>mac|output|...</c> for playback), with uid and name trimmed.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when uid or name is null or whitespace.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="direction"/> is not a defined direction or
        /// <paramref name="channels"/> is not positive.
        /// </exception>
        public static string BuildKey(
            AudioDeviceDirection direction, string uid, string name, int channels)
        {
            if (!Enum.IsDefined(typeof(AudioDeviceDirection), direction))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(direction), direction, "An audio device direction is required.");
            }

            if (string.IsNullOrWhiteSpace(uid))
            {
                throw new ArgumentException("A device uid is required.", nameof(uid));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A device name is required.", nameof(name));
            }

            if (channels <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(channels), channels, "A device has at least one channel.");
            }

            var directionToken = direction == AudioDeviceDirection.Input ? "input" : "output";
            return string.Concat(
                "mac|", directionToken, "|", uid.Trim(), "|", name.Trim(), "|", channels.ToString());
        }

        /// <summary>
        /// True only for the default-device marker: null, empty or whitespace.
        /// A concrete Mac key (or a foreign platform sentinel) is never default.
        /// </summary>
        public static bool IsDefaultKey(string? key) => string.IsNullOrWhiteSpace(key);

        /// <summary>
        /// Compares two device keys for identity. Default forms (null, empty,
        /// whitespace) match only each other; concrete keys compare
        /// case-insensitively because CoreAudio UIDs and display names are not
        /// case-stable across OS versions.
        /// </summary>
        public static bool Matches(string? first, string? second)
        {
            var firstIsDefault = IsDefaultKey(first);
            var secondIsDefault = IsDefaultKey(second);

            if (firstIsDefault || secondIsDefault)
            {
                return firstIsDefault && secondIsDefault;
            }

            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Converts a concrete device to its <see cref="AudioDeviceId"/>, never
        /// the empty default marker.
        /// </summary>
        public static AudioDeviceId ToDeviceId(
            AudioDeviceDirection direction, string uid, string name, int channels)
        {
            return AudioDeviceId.FromKey(BuildKey(direction, uid, name, channels));
        }

        /// <summary>
        /// The default-device marker: the shared empty <see cref="AudioDeviceId.Default"/>.
        /// </summary>
        public static AudioDeviceId ToDefaultDeviceId() => AudioDeviceId.Default;
    }
}
