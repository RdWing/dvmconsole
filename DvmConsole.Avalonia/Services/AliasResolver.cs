// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using dvmconsole;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Per-system subscriber-alias lookup for the call-history slice
    /// (the alias.yml follow-on; audit deleg_79328deb recorded the
    /// deferral). The resolver holds the alias lists loaded per
    /// codeplug system and resolves a (system name, radio id) pair to
    /// the subscriber alias, WPF-parity with the console's RID lookup
    /// (MainWindow.xaml.cs:1064-1065 and <see cref="AliasTools.GetAliasByRid"/>).
    /// System lookup is ordinal case-insensitive (ReceiveChannelResolver
    /// convention). Never throws: unknown systems, empty alias lists
    /// and unmatched ids all resolve to null.
    /// </summary>
    public sealed class AliasResolver
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<RadioAlias>> aliasesBySystem;

        /// <summary>
        /// Creates a resolver over the per-system alias lists.
        /// </summary>
        /// <param name="aliasesBySystem">Map from the FNE system name to its loaded alias list.</param>
        public AliasResolver(IReadOnlyDictionary<string, IReadOnlyList<RadioAlias>> aliasesBySystem)
        {
            if (aliasesBySystem is null)
            {
                throw new ArgumentNullException(nameof(aliasesBySystem));
            }

            var snapshot = new Dictionary<string, IReadOnlyList<RadioAlias>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var pair in aliasesBySystem)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                var aliases = new List<RadioAlias>();
                if (pair.Value is not null)
                {
                    foreach (var alias in pair.Value)
                    {
                        if (alias is null)
                        {
                            continue;
                        }

                        aliases.Add(new RadioAlias
                        {
                            Rid = alias.Rid,
                            Alias = alias.Alias,
                        });
                    }
                }

                snapshot[pair.Key] = aliases;
            }

            this.aliasesBySystem = snapshot;
        }

        /// <summary>
        /// Resolves the subscriber alias for a radio id on a system, or
        /// null when the system is unknown / has no aliases / the id is
        /// unmatched. System lookup is ordinal case-insensitive. Never
        /// throws.
        /// </summary>
        /// <param name="systemName">The FNE system name.</param>
        /// <param name="srcId">The transmitting radio id.</param>
        /// <returns>The subscriber alias, or null when unresolvable.</returns>
        public string? Resolve(string systemName, uint srcId)
        {
            if (string.IsNullOrEmpty(systemName))
            {
                return null;
            }

            if (!aliasesBySystem.TryGetValue(systemName, out var aliases)
                || aliases.Count == 0)
            {
                return null;
            }

            foreach (var alias in aliases)
            {
                if (alias.Rid == (int)srcId)
                {
                    return string.IsNullOrEmpty(alias.Alias) ? null : alias.Alias;
                }
            }

            return null;
        }
    }
}
