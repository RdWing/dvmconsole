// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Pure mapper from a saved audio-device id to the matching
    /// <see cref="AudioDeviceOptionViewModel"/> row of a view-model device
    /// list. The default marker resolves to the first row whose
    /// <see cref="AudioDeviceId.IsDefault"/> is set; a non-default id
    /// resolves to the first row whose <see cref="AudioDeviceId.Value"/>
    /// matches case-insensitively, including unavailable saved rows. This
    /// class is a pure function: no catalog, UI, native, network, file, or
    /// persistence access, and the options list is never altered.
    /// </summary>
    public static class AudioDeviceSelectionMapper
    {
        /// <summary>
        /// Finds the option row matching the saved id, or null when there
        /// is no match.
        /// </summary>
        /// <param name="options">The device option rows to search; must not be null.</param>
        /// <param name="id">The saved device id, or null for no selection.</param>
        /// <returns>
        /// The first matching row: the default-marked row for
        /// <see cref="AudioDeviceId.Default"/>, the first row with a
        /// case-insensitively equal <see cref="AudioDeviceId.Value"/> for a
        /// non-default id, or null when no row matches.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> is null.
        /// </exception>
        public static AudioDeviceOptionViewModel? FindById(
            IReadOnlyList<AudioDeviceOptionViewModel> options,
            AudioDeviceId? id)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (id is not { } wanted)
            {
                return null;
            }

            if (wanted.IsDefault)
            {
                foreach (var option in options)
                {
                    if (option.Id.IsDefault)
                    {
                        return option;
                    }
                }

                return null;
            }

            foreach (var option in options)
            {
                if (!option.Id.IsDefault
                    && string.Equals(option.Id.Value, wanted.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return option;
                }
            }

            return null;
        }
    }
}
