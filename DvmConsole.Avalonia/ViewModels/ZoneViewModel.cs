// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Immutable view-model for one codeplug zone in the dashboard's
    /// zone selector. Wraps the codeplug's <see cref="Codeplug.Zone"/>
    /// wholesale: the zone name and its channel list are captured at
    /// construction and never change afterwards. The channel list is
    /// passed through as-is and may be null (a codeplug zone may carry
    /// no channel list); the zone selector and slot-assignment loop
    /// treat a null list as an empty zone. No INPC surface: the zone
    /// itself is static data, and selection is tracked on the
    /// containing dashboard view-model (<see cref="MainWindowViewModel.SelectedZone"/>).
    /// </summary>
    public sealed class ZoneViewModel
    {
        /// <summary>
        /// Creates a zone view-model for the given name and channel
        /// list. The channel list is stored by reference and may be
        /// null; no copy or defensive snapshot is taken.
        /// </summary>
        /// <param name="name">The zone's display name.</param>
        /// <param name="channels">
        /// The zone's channels in codeplug order, or null when the
        /// codeplug zone carries no channel list.
        /// </param>
        public ZoneViewModel(string name, IReadOnlyList<Codeplug.Channel>? channels)
        {
            Name = name;
            Channels = channels;
        }

        /// <summary>The zone's display name.</summary>
        public string Name { get; }

        /// <summary>
        /// The zone's channels in codeplug order, or null when the
        /// codeplug zone carries no channel list.
        /// </summary>
        public IReadOnlyList<Codeplug.Channel>? Channels { get; }
    }
}
