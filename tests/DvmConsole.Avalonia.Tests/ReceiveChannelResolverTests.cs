// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the Call History slice (audit deleg_79328deb
* READY) — DvmConsole.Avalonia.Services.ReceiveChannelResolver:
*
*   - Sealed; ctor ReceiveChannelResolver(Codeplug codeplug) — null
*     throws ArgumentNullException (TransmitTargetResolver mirror).
*   - string? Resolve(string systemName, uint dstId, byte? slot):
*     first-zone-wins scan of codeplug zones; channel matches when
*     System equals systemName (OrdinalIgnoreCase) AND Tgid parses to
*     dstId AND (P25 ignores slot; DMR slot matches channel.Slot+1 —
*     codeplug Slot is 1-based); never throws (null zones, null
*     channel lists, malformed Tgid, missing system all -> null).
*   - Resolves a human channel name for the call-history display;
*     null means "raw key fallback" for the caller.
*/
using System;
using System.Collections.Generic;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="ReceiveChannelResolver"/>.
    /// </summary>
    public sealed class ReceiveChannelResolverTests
    {
        /* ------------------------------------------------------------------
        ** Surface
        ** ---------------------------------------------------------------- */

        [Fact]
        public void ApiShape_ExactSurface()
        {
            var type = typeof(ReceiveChannelResolver);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(Codeplug) }));
            Assert.NotNull(type.GetMethod(nameof(ReceiveChannelResolver.Resolve), new[]
            {
                typeof(string), typeof(uint), typeof(byte),
            }));
        }

        [Fact]
        public void NullCodeplug_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ReceiveChannelResolver(null!));
        }

        /* ------------------------------------------------------------------
        ** Fixture
        ** ---------------------------------------------------------------- */

        private static Codeplug MakeCodeplug()
        {
            return new Codeplug
            {
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone
                    {
                        Name = "Zone A",
                        Channels = new List<Codeplug.Channel>
                        {
                            new Codeplug.Channel { Name = "CH 1 DMR", System = "Repeater 1", Tgid = "31001", Slot = 1, Mode = "dmr" },
                            new Codeplug.Channel { Name = "CH 2 P25", System = "Repeater 1", Tgid = "31002", Slot = 2, Mode = "p25" },
                            new Codeplug.Channel { Name = "CH 3", System = "Repeater 1", Tgid = "31003", Slot = 1, Mode = "dmr" },
                            new Codeplug.Channel { Name = "CH 4", System = "Repeater 1", Tgid = "31004", Slot = 2, Mode = "dmr" },
                        },
                    },
                    new Codeplug.Zone
                    {
                        Name = "Zone B",
                        Channels = new List<Codeplug.Channel>
                        {
                            new Codeplug.Channel { Name = "B1", System = "Repeater 2", Tgid = "32001", Slot = 1, Mode = "dmr" },
                        },
                    },
                    new Codeplug.Zone
                    {
                        Name = "Zone C (empty)",
                        Channels = null,
                    },
                },
            };
        }

        /* ------------------------------------------------------------------
        ** Resolution
        /* ---------------------------------------------------------------- */

        [Fact]
        public void Resolve_Dmr_MatchBySystemTgidSlot()
        {
            var resolver = new ReceiveChannelResolver(MakeCodeplug());

            // Wire slot is 0-based (FnePeer.cs:786); codeplug Slot is
            // 1-based (WPF MainWindow.DMR.cs:48 Slot-1). CH 1 DMR has
            // codeplug Slot=1 => wire slot 0.
            Assert.Equal("CH 1 DMR", resolver.Resolve("Repeater 1", 31001, 0));
            Assert.Equal("CH 3", resolver.Resolve("Repeater 1", 31003, 0));
            // CH 4 has codeplug Slot=2 => wire slot 1.
            Assert.Equal("CH 4", resolver.Resolve("Repeater 1", 31004, 1));
            // P25 ignores slot.
            Assert.Equal("CH 2 P25", resolver.Resolve("Repeater 1", 31002, 2));
        }

        [Fact]
        public void Resolve_SystemCaseInsensitive()
        {
            var resolver = new ReceiveChannelResolver(MakeCodeplug());

            Assert.Equal("CH 1 DMR", resolver.Resolve("repeater 1", 31001, 0));
            Assert.Equal("CH 1 DMR", resolver.Resolve("REPEATER 1", 31001, 0));
        }

        [Fact]
        public void Resolve_Dmr_SlotMismatch_Null()
        {
            var resolver = new ReceiveChannelResolver(MakeCodeplug());

            // CH 1 DMR is codeplug slot 1 (wire slot 0); wire slot 1
            // must not match it.
            Assert.Null(resolver.Resolve("Repeater 1", 31001, 1));
        }

        [Fact]
        public void Resolve_Dmr_SameTgidBothSlots_Distinguished()
        {
            var codeplug = MakeCodeplug();
            // Give tgid 31005 to both codeplug slots in different zones
            // so the 0/1 wire convention is provably load-bearing.
            codeplug.Zones[0].Channels!.Add(new Codeplug.Channel { Name = "CH 5 A", System = "Repeater 1", Tgid = "31005", Slot = 1, Mode = "dmr" });
            codeplug.Zones[0].Channels!.Add(new Codeplug.Channel { Name = "CH 5 B", System = "Repeater 1", Tgid = "31005", Slot = 2, Mode = "dmr" });
            var resolver = new ReceiveChannelResolver(codeplug);

            Assert.Equal("CH 5 A", resolver.Resolve("Repeater 1", 31005, 0));
            Assert.Equal("CH 5 B", resolver.Resolve("Repeater 1", 31005, 1));
        }

        [Fact]
        public void Resolve_P25_IgnoresSlot()
        {
            var resolver = new ReceiveChannelResolver(MakeCodeplug());

            Assert.Equal("CH 2 P25", resolver.Resolve("Repeater 1", 31002, 1)); // slot ignored
            Assert.Equal("CH 2 P25", resolver.Resolve("Repeater 1", 31002, null));
        }

        [Fact]
        public void Resolve_NullSlot_Dmr_FallsBackToAny()
        {
            var resolver = new ReceiveChannelResolver(MakeCodeplug());

            Assert.Equal("CH 1 DMR", resolver.Resolve("Repeater 1", 31001, null));
        }

        [Fact]
        public void Resolve_WireSlotOne_CodeplugSlotTwo()
        {
            var resolver = new ReceiveChannelResolver(MakeCodeplug());

            // CH 4 is codeplug Slot=2 => matches wire slot 1 only.
            Assert.Equal("CH 4", resolver.Resolve("Repeater 1", 31004, 1));
            Assert.Null(resolver.Resolve("Repeater 1", 31004, 0));
        }

        [Fact]
        public void Resolve_FirstZoneWins()
        {
            var resolver = new ReceiveChannelResolver(MakeCodeplug());

            // "B1" is in Zone B; nothing in Zone A has tgid 32001, so the
            // first match is Zone B's. B1 is codeplug slot 1 => wire slot 0.
            Assert.Equal("B1", resolver.Resolve("Repeater 2", 32001, 0));
        }

        [Fact]
        public void Resolve_UnknownSystem_Null()
        {
            var resolver = new ReceiveChannelResolver(MakeCodeplug());

            Assert.Null(resolver.Resolve("No Such System", 31001, 1));
        }

        [Fact]
        public void Resolve_UnknownTgid_Null()
        {
            var resolver = new ReceiveChannelResolver(MakeCodeplug());

            Assert.Null(resolver.Resolve("Repeater 1", 99999, 1));
        }

        [Fact]
        public void Resolve_MalformedTgid_Null_NeverThrows()
        {
            var codeplug = MakeCodeplug();
            codeplug.Zones[0].Channels![0].Tgid = "not-a-number";
            var resolver = new ReceiveChannelResolver(codeplug);

            Assert.Null(resolver.Resolve("Repeater 1", 31001, 1));
        }

        [Fact]
        public void Resolve_NullZoneChannels_Null_NeverThrows()
        {
            var resolver = new ReceiveChannelResolver(MakeCodeplug());

            Assert.Null(resolver.Resolve("Repeater 1", 99999, 1)); // walks Zone C (null Channels)
        }
    }
}
