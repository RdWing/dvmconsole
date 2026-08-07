// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Resolves a received frame's target (system name, destination
    /// talkgroup id, DMR slot) onto a human codeplug channel name for
    /// the call-history display, with <see cref="TransmitTargetResolver"/>
    /// mirror semantics: a first-zone-wins scan of the codeplug zones,
    /// total and never throwing. A channel matches when its system
    /// equals the frame's system (OrdinalIgnoreCase), its Tgid parses
    /// to the frame's destination id, and — for non-P25 channels with a
    /// known slot — its codeplug slot equals the frame's slot (the
    /// codeplug slot is 1-based, WPF MainWindow.DMR.cs:48 parity). P25
    /// channels and null slots ignore the slot entirely. Null means
    /// "raw-key fallback" for the caller.
    /// </summary>
    public sealed class ReceiveChannelResolver
    {
        private readonly Codeplug codeplug;

        /// <summary>
        /// Creates a resolver over the given codeplug.
        /// </summary>
        /// <param name="codeplug">The codeplug to resolve channels against.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="codeplug"/> is null.
        /// </exception>
        public ReceiveChannelResolver(Codeplug codeplug)
        {
            this.codeplug = codeplug ?? throw new ArgumentNullException(nameof(codeplug));
        }

        /// <summary>
        /// Resolves the first codeplug channel matching the frame's
        /// system, destination talkgroup id and slot, or null on a miss.
        /// Total and never throws: null zones, null channel lists, null
        /// channel entries, malformed Tgids and unknown systems all
        /// degrade to null.
        /// </summary>
        /// <param name="systemName">The FNE system name the frame arrived on.</param>
        /// <param name="dstId">The destination talkgroup id.</param>
        /// <param name="slot">The DMR slot, or null when the frame carries no slot concept.</param>
        /// <returns>The first matching channel's name, or null.</returns>
        public string? Resolve(string systemName, uint dstId, byte? slot)
        {
            if (codeplug.Zones is null)
            {
                return null;
            }

            foreach (var zone in codeplug.Zones)
            {
                if (zone is null || zone.Channels is null)
                {
                    continue;
                }

                foreach (var channel in zone.Channels)
                {
                    if (channel is null
                        || !string.Equals(channel.System, systemName, StringComparison.OrdinalIgnoreCase)
                        || !uint.TryParse(channel.Tgid, out var tgid)
                        || tgid != dstId)
                    {
                        continue;
                    }

                    // P25 channels and null slots ignore the slot;
                    // everything else (DMR/NXDN) requires an exact
                    // slot match: the wire slot is 0-based (FnePeer.cs:
                    // 772), the codeplug slot is 1-based (WPF
                    // MainWindow.DMR.cs:48 Slot-1), so +1.
                    if (channel.GetChannelMode() != Codeplug.ChannelMode.P25
                        && slot is not null
                        && channel.Slot != slot.Value + 1)
                    {
                        continue;
                    }

                    return channel.Name;
                }
            }

            return null;
        }
    }
}
