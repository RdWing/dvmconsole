// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Linq;
using dvmconsole;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Maps a selected channel NAME onto the router's
    /// <see cref="TransmitTarget"/>, making the shell's PTT path real.
    /// WPF parity with shell degrade-not-throw semantics: null/blank
    /// name, unknown channel, RxOnly channel, NXDN mode (the router is
    /// DMR/P25 only), a missing system, and malformed Rid/Tgid all
    /// resolve to null — never throw (the resolver runs on the PTT-down
    /// UI path and the sender re-parses at send time). SourceId comes
    /// from <c>uint.Parse(system.Rid)</c> (WPF MainWindow.DMR.cs:49);
    /// Slot passes through as byte (1-based, WPF MainWindow.DMR.cs:48);
    /// mode comes from <c>Codeplug.Channel.GetChannelMode</c>
    /// (case-insensitive).
    /// </summary>
    public sealed class TransmitTargetResolver
    {
        private readonly Codeplug codeplug;

        /// <summary>
        /// Creates a resolver over the given codeplug.
        /// </summary>
        /// <param name="codeplug">The codeplug to resolve channels against.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="codeplug"/> is null.
        /// </exception>
        public TransmitTargetResolver(Codeplug codeplug)
        {
            this.codeplug = codeplug ?? throw new ArgumentNullException(nameof(codeplug));
        }

        /// <summary>
        /// Resolves the channel with the given name onto a transmit
        /// target, or null when the channel cannot transmit. Total and
        /// never throws: null/blank name, unknown channel
        /// (first-zone-wins), RxOnly channel, NXDN mode (the shell
        /// router is DMR/P25 only), a system not present in the
        /// codeplug, or a Rid/Tgid that does not parse as a uint all
        /// degrade to null.
        /// </summary>
        /// <param name="channelName">The codeplug channel name to resolve.</param>
        /// <returns>
        /// The transmit target for the channel, or null when the
        /// channel cannot transmit.
        /// </returns>
        public TransmitTarget? Resolve(string? channelName)
        {
            if (string.IsNullOrWhiteSpace(channelName))
            {
                return null;
            }

            if (codeplug.Zones is null)
            {
                return null;
            }

            // Iterate zones defensively instead of delegating to the Core
            // GetChannelByName helper, which throws on a zone whose
            // Channels list is null (structurally valid YAML can omit the
            // channels: key). First-zone-wins, exact-name match, and
            // null-entry guards preserve GetChannelByName's semantics
            // without its unguarded path.
            var channel = codeplug.Zones
                .SelectMany(zone => zone.Channels ?? Enumerable.Empty<Codeplug.Channel>())
                .FirstOrDefault(c => c is not null && c.Name == channelName);
            if (channel is null || channel.RxOnly)
            {
                return null;
            }

            var mode = channel.GetChannelMode();
            if (mode == Codeplug.ChannelMode.NXDN)
            {
                // The shell router is DMR/P25 only
                // (TalkgroupAudioRouter.cs:350).
                return null;
            }

            if (codeplug.Systems is null)
            {
                return null;
            }

            var system = codeplug.Systems.FirstOrDefault(s => s.Name == channel.System);
            if (system is null)
            {
                return null;
            }

            if (!uint.TryParse(system.Rid, out var sourceId)
                || !uint.TryParse(channel.Tgid, out _))
            {
                // WPF uint.Parse would throw; the shell degrades — the
                // sender re-parses at send time.
                return null;
            }

            return new TransmitTarget(
                system.Name,
                channel.Tgid,
                (byte)channel.Slot,
                mode == Codeplug.ChannelMode.P25 ? VoiceMode.P25 : VoiceMode.Dmr,
                sourceId);
        }

        /// <summary>
        /// Recovers the codeplug channel name for a resolved target without
        /// changing the Platform target record shape.
        /// </summary>
        public string? ResolveChannelName(TransmitTarget target)
        {
            if (codeplug.Zones is null)
                return null;

            return codeplug.Zones
                .SelectMany(zone => zone.Channels ?? Enumerable.Empty<Codeplug.Channel>())
                .Where(channel => channel is not null && !channel.RxOnly)
                .Where(channel => string.Equals(channel.System, target.SystemName, StringComparison.Ordinal))
                .Where(channel => string.Equals(channel.Tgid, target.TalkgroupId, StringComparison.Ordinal))
                .Where(channel => (byte)channel.Slot == target.Slot)
                .Where(channel => channel.GetChannelMode() == (target.Mode == VoiceMode.P25
                    ? Codeplug.ChannelMode.P25
                    : Codeplug.ChannelMode.DMR))
                .FirstOrDefault(channel => codeplug.Systems?.Any(system =>
                    string.Equals(system.Name, target.SystemName, StringComparison.Ordinal)
                    && uint.TryParse(system.Rid, out uint sourceId)
                    && sourceId == target.SourceId) == true)
                ?.Name;
        }

        /// <summary>
        /// Resolves each channel name in order onto a transmit target,
        /// skipping names that resolve to null, and returns the targets
        /// in input order. Total and never throws: a null or empty
        /// input yields an empty list, and every name degrades exactly
        /// as <see cref="Resolve"/> (null/blank, unknown, RxOnly, NXDN,
        /// missing system, malformed Rid/Tgid — all skipped, never
        /// thrown). Powers the AllChannels PTT fan-out: the shell
        /// projects the engaged slots' channel names onto this method.
        /// </summary>
        /// <param name="channelNames">The codeplug channel names to resolve.</param>
        /// <returns>
        /// The transmit targets for the resolvable channels in input
        /// order; empty when the input is null/empty or no channel
        /// resolves.
        /// </returns>
        public IReadOnlyList<TransmitTarget> ResolveAll(IEnumerable<string?>? channelNames)
        {
            if (channelNames is null)
            {
                return Array.Empty<TransmitTarget>();
            }

            var targets = new List<TransmitTarget>();
            foreach (var channelName in channelNames)
            {
                var target = Resolve(channelName);
                if (target is not null)
                {
                    targets.Add(target.Value);
                }
            }

            return targets;
        }
    }
}
