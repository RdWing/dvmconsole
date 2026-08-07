// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the alias.yml follow-on slice (Call History
* panel's empty RID Alias column; audit deleg_79328deb recorded the
* deferral) — DvmConsole.Avalonia.Services.AliasResolver:
*
*   - Sealed; ctor AliasResolver(IReadOnlyDictionary<string,
*     IReadOnlyList<RadioAlias>> aliasesBySystem) — a map from the
*     FNE system name to its loaded alias list; null throws
*     ArgumentNullException.
*   - string? Resolve(string systemName, uint srcId): the alias for
*     the radio id on that system (AliasTools.GetAliasByRid parity),
*     or null when the system is unknown / has no aliases / the id is
*     unmatched. System lookup is OrdinalIgnoreCase (ReceiveChannel
*     Resolver convention). Never throws.
*/
using System;
using System.Collections.Generic;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="AliasResolver"/>.
    /// </summary>
    public sealed class AliasResolverTests
    {
        /* ------------------------------------------------------------------
        ** Surface
        ** ---------------------------------------------------------------- */

        [Fact]
        public void ApiShape_ExactSurface()
        {
            var type = typeof(AliasResolver);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[]
            {
                typeof(IReadOnlyDictionary<string, IReadOnlyList<RadioAlias>>),
            }));
            Assert.NotNull(type.GetMethod(nameof(AliasResolver.Resolve), new[]
            {
                typeof(string), typeof(uint),
            }));
        }

        [Fact]
        public void NullMap_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new AliasResolver(null!));
        }

        /* ------------------------------------------------------------------
        ** Resolution
        ** ---------------------------------------------------------------- */

        private static AliasResolver MakeResolver()
        {
            return new AliasResolver(new Dictionary<string, IReadOnlyList<RadioAlias>>
            {
                ["Repeater 1"] = new List<RadioAlias>
                {
                    new RadioAlias { Rid = 1001, Alias = "Alpha Base" },
                    new RadioAlias { Rid = 1002, Alias = "Bravo Repeater" },
                },
                ["Repeater 2"] = new List<RadioAlias>(),
            });
        }

        [Fact]
        public void Resolve_KnownSystemAndRid_ReturnsAlias()
        {
            var resolver = MakeResolver();

            Assert.Equal("Alpha Base", resolver.Resolve("Repeater 1", 1001));
            Assert.Equal("Bravo Repeater", resolver.Resolve("Repeater 1", 1002));
        }

        [Fact]
        public void Resolve_SystemCaseInsensitive()
        {
            var resolver = MakeResolver();

            Assert.Equal("Alpha Base", resolver.Resolve("repeater 1", 1001));
            Assert.Equal("Alpha Base", resolver.Resolve("REPEATER 1", 1001));
        }

        [Fact]
        public void Resolve_UnknownSystem_Null()
        {
            var resolver = MakeResolver();

            Assert.Null(resolver.Resolve("No Such System", 1001));
        }

        [Fact]
        public void Resolve_UnmatchedRid_Null()
        {
            var resolver = MakeResolver();

            Assert.Null(resolver.Resolve("Repeater 1", 9999));
        }

        [Fact]
        public void Resolve_SystemWithEmptyAliasList_Null()
        {
            var resolver = MakeResolver();

            Assert.Null(resolver.Resolve("Repeater 2", 1001));
        }

        [Fact]
        public void Resolve_NullOrEmptySystemName_Null_NeverThrows()
        {
            var resolver = MakeResolver();

            Assert.Null(resolver.Resolve(null!, 1001));
            Assert.Null(resolver.Resolve("", 1001));
        }

        [Fact]
        public void Resolve_NullAliasRow_Null_NeverThrows()
        {
            var resolver = new AliasResolver(new Dictionary<string, IReadOnlyList<RadioAlias>>
            {
                ["Repeater 1"] = new List<RadioAlias> { null! },
            });

            Assert.Null(resolver.Resolve("Repeater 1", 1001));
        }

        [Fact]
        public void Constructor_SnapshotsAliasData()
        {
            var aliases = new List<RadioAlias>
            {
                new RadioAlias { Rid = 1001, Alias = "Alpha Base" },
            };
            var map = new Dictionary<string, IReadOnlyList<RadioAlias>>
            {
                ["Repeater 1"] = aliases,
            };
            var resolver = new AliasResolver(map);

            aliases[0].Alias = "Mutated";
            map.Clear();

            Assert.Equal("Alpha Base", resolver.Resolve("Repeater 1", 1001));
        }
    }
}
