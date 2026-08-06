// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Read-only option row for one selectable audio device in the audio
    /// settings view: an opaque device id, a human-readable name, and
    /// whether the device is currently available. This is a pure
    /// presentation row built by the view-model; it carries no native
    /// handle, secret, or UI surface.
    /// </summary>
    public sealed class AudioDeviceOptionViewModel
    {
        /// <summary>
        /// Creates an option row projecting the given values verbatim.
        /// </summary>
        /// <param name="id">Opaque device id.</param>
        /// <param name="name">Human-readable device name.</param>
        /// <param name="isAvailable">True when the device is currently available.</param>
        public AudioDeviceOptionViewModel(AudioDeviceId id, string name, bool isAvailable)
        {
            Id = id;
            Name = name;
            IsAvailable = isAvailable;
        }

        /// <summary>Opaque device id.</summary>
        public AudioDeviceId Id { get; }

        /// <summary>Human-readable device name.</summary>
        public string Name { get; }

        /// <summary>True when the device is currently available.</summary>
        public bool IsAvailable { get; }
    }
}
