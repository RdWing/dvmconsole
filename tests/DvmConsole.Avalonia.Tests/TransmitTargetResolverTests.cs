// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the transmit-target resolver slice (plan Task 9
* step 4 / vertical-slice gate item 4-6: transmit via in-window PTT):
*
*   DvmConsole.Avalonia.Services.TransmitTargetResolver
*
* The resolver maps a selected channel NAME onto the router's
* TransmitTarget(SystemName, TalkgroupId, Slot, Mode, SourceId),
* making the shell's PTT path real (MainWindow.ResolveTransmitTarget
* is the documented no-op today). WPF parity with shell degrade-not-
* throw semantics: null/blank name, unknown channel, RxOnly channel,
* NXDN mode (the router is DMR/P25 only), missing system, and
* malformed Rid/Tgid all resolve to null — NEVER throw (the resolver
* runs on the PTT-down UI path and the sender re-parses at send
* time). SourceId = uint.Parse(system.Rid) (WPF MainWindow.DMR.cs:49);
* Slot passes through as byte (1-based, WPF MainWindow.DMR.cs:48);
* mode via Codeplug.Channel.GetChannelMode (case-insensitive).
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DvmConsole.Avalonia.Services;
using dvmconsole;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="TransmitTargetResolver"/>.
    /// </summary>
    public sealed class TransmitTargetResolverTests
    {
        /* ------------------------------------------------------------------
        ** Fixture
        ** ---------------------------------------------------------------- */

        private static Codeplug MakeCodeplug()
        {
            var codeplug = new Codeplug
            {
                Systems = new System.Collections.Generic.List<Codeplug.System>
                {
                    new Codeplug.System
                    {
                        Name = "Repeater 1",
                        Rid = "1000001",
                        Address = "127.0.0.1",
                        Port = 62031,
                        PeerId = 1,
                    },
                    new Codeplug.System
                    {
                        Name = "Repeater 2",
                        Rid = "2000002",
                        Address = "127.0.0.2",
                        Port = 62032,
                        PeerId = 2,
                    },
                },
                Zones = new System.Collections.Generic.List<Codeplug.Zone>
                {
                    new Codeplug.Zone
                    {
                        Name = "Zone A",
                        Channels = new System.Collections.Generic.List<Codeplug.Channel>
                        {
                            new Codeplug.Channel { Name = "CH 1 DMR", System = "Repeater 1", Tgid = "31001", Slot = 1, Mode = "dmr" },
                            new Codeplug.Channel { Name = "CH 2 P25", System = "Repeater 1", Tgid = "31002", Slot = 2, Mode = "p25" },
                            new Codeplug.Channel { Name = "CH 3 RX", System = "Repeater 1", Tgid = "31003", Slot = 1, Mode = "dmr", RxOnly = true },
                            new Codeplug.Channel { Name = "CH 4 NXDN", System = "Repeater 2", Tgid = "31004", Slot = 1, Mode = "nxdn" },
                            new Codeplug.Channel { Name = "CH 5 MissingSys", System = "No Such System", Tgid = "31005", Slot = 1, Mode = "dmr" },
                        },
                    },
                },
            };
            return codeplug;
        }

        /* ------------------------------------------------------------------
        ** Surface
        ** ---------------------------------------------------------------- */

        [Fact]
        public void ApiShape_ExactPublicSurface()
        {
            var type = typeof(TransmitTargetResolver);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(Codeplug) }));
            Assert.NotNull(type.GetMethod("Resolve", new[] { typeof(string) }));
            Assert.Equal(typeof(TransmitTarget?), type.GetMethod("Resolve", new[] { typeof(string) })!.ReturnType);
        }

        [Fact]
        public void Ctor_NullCodeplug_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new TransmitTargetResolver(null!));
        }

        /* ------------------------------------------------------------------
        ** Degrade paths (never throw)
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Resolve_NullOrBlankName_Null()
        {
            var resolver = new TransmitTargetResolver(MakeCodeplug());
            Assert.Null(resolver.Resolve(null));
            Assert.Null(resolver.Resolve(""));
            Assert.Null(resolver.Resolve("   "));
        }

        [Fact]
        public void Resolve_UnknownChannel_Null()
        {
            var resolver = new TransmitTargetResolver(MakeCodeplug());
            Assert.Null(resolver.Resolve("No Such Channel"));
        }

        [Fact]
        public void Resolve_RxOnlyChannel_Null()
        {
            var resolver = new TransmitTargetResolver(MakeCodeplug());
            Assert.Null(resolver.Resolve("CH 3 RX"));
        }

        [Fact]
        public void Resolve_NxdnChannel_Null()
        {
            // The shell router is DMR/P25 only.
            var resolver = new TransmitTargetResolver(MakeCodeplug());
            Assert.Null(resolver.Resolve("CH 4 NXDN"));
        }

        [Fact]
        public void Resolve_SystemNotInCodeplug_Null()
        {
            var resolver = new TransmitTargetResolver(MakeCodeplug());
            Assert.Null(resolver.Resolve("CH 5 MissingSys"));
        }

        [Fact]
        public void Resolve_MalformedRid_Null_NoThrow()
        {
            var codeplug = MakeCodeplug();
            codeplug.Systems[0].Rid = "not-a-number";
            var resolver = new TransmitTargetResolver(codeplug);

            Assert.Null(resolver.Resolve("CH 1 DMR")); // no throw
        }

        [Fact]
        public void Resolve_MalformedTgid_Null_NoThrow()
        {
            var codeplug = MakeCodeplug();
            codeplug.Zones[0].Channels[0].Tgid = "oops";
            var resolver = new TransmitTargetResolver(codeplug);

            Assert.Null(resolver.Resolve("CH 1 DMR")); // no throw
        }

        /* ------------------------------------------------------------------
        ** Happy paths
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Resolve_DmrChannel_ExactTarget()
        {
            var resolver = new TransmitTargetResolver(MakeCodeplug());

            var target = resolver.Resolve("CH 1 DMR");

            Assert.NotNull(target);
            Assert.Equal("Repeater 1", target!.Value.SystemName);
            Assert.Equal("31001", target.Value.TalkgroupId);
            Assert.Equal(1, target.Value.Slot);
            Assert.Equal(VoiceMode.Dmr, target.Value.Mode);
            Assert.Equal(1000001u, target.Value.SourceId);
        }

        [Fact]
        public void Resolve_P25Channel_P25Mode()
        {
            var resolver = new TransmitTargetResolver(MakeCodeplug());

            var target = resolver.Resolve("CH 2 P25");

            Assert.NotNull(target);
            Assert.Equal(VoiceMode.P25, target!.Value.Mode);
            Assert.Equal(2, target.Value.Slot);
            Assert.Equal(1000001u, target.Value.SourceId);
        }

        [Fact]
        public void Resolve_ModeCaseInsensitive()
        {
            var codeplug = MakeCodeplug();
            codeplug.Zones[0].Channels[0].Mode = "DMR"; // uppercase
            codeplug.Zones[0].Channels[1].Mode = "P25"; // uppercase
            var resolver = new TransmitTargetResolver(codeplug);

            Assert.Equal(VoiceMode.Dmr, resolver.Resolve("CH 1 DMR")!.Value.Mode);
            Assert.Equal(VoiceMode.P25, resolver.Resolve("CH 2 P25")!.Value.Mode);
        }

        [Fact]
        public void Resolve_SlotPassthrough_OneAndTwo()
        {
            var resolver = new TransmitTargetResolver(MakeCodeplug());

            Assert.Equal((byte)1, resolver.Resolve("CH 1 DMR")!.Value.Slot);
            Assert.Equal((byte)2, resolver.Resolve("CH 2 P25")!.Value.Slot);
        }

        [Fact]
        public void Resolve_FirstZoneWins()
        {
            var codeplug = MakeCodeplug();
            codeplug.Zones.Add(new Codeplug.Zone
            {
                Name = "Zone B",
                Channels = new System.Collections.Generic.List<Codeplug.Channel>
                {
                    new Codeplug.Channel { Name = "CH 1 DMR", System = "Repeater 2", Tgid = "99999", Slot = 2, Mode = "p25" },
                },
            });
            var resolver = new TransmitTargetResolver(codeplug);

            // GetChannelByName returns the FIRST zone match (Zone A).
            var target = resolver.Resolve("CH 1 DMR");
            Assert.Equal("Repeater 1", target!.Value.SystemName);
            Assert.Equal("31001", target.Value.TalkgroupId);
        }

        [Fact]
        public void Resolve_ZoneWithoutChannelsList_Null_NoThrow()
        {
            // Structurally valid YAML can carry a zone with no channels:
            // key at all. GetChannelByName (Core) would throw on the null
            // list — the resolver's TOTAL contract must not delegate to
            // that unguarded path.
            var codeplug = MakeCodeplug();
            codeplug.Zones.Add(new Codeplug.Zone { Name = "Empty Zone" });
            var resolver = new TransmitTargetResolver(codeplug);

            Assert.NotNull(resolver.Resolve("CH 1 DMR")); // zone A still resolves
            Assert.Null(resolver.Resolve("Nothing Here")); // no throw on the empty zone
        }

        [Fact]
        public void Resolve_WpfParity_SlotAndTgidMapping()
        {
            // WPF MainWindow.DMR.cs:48-50: slot 1-based, tgid parsed from
            // the channel, srcId from system.Rid. The resolver mirrors
            // that mapping into TransmitTarget.
            var resolver = new TransmitTargetResolver(MakeCodeplug());
            var target = resolver.Resolve("CH 2 P25")!.Value;

            Assert.Equal("31002", target.TalkgroupId);
            Assert.Equal((byte)2, target.Slot);
            Assert.Equal(1000001u, target.SourceId);
        }

        [Fact]
        public void ResolveAll_ApiShape_IsOrderedTargetList()
        {
            var method = typeof(TransmitTargetResolver)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == "ResolveAll");

            Assert.NotNull(method);
            Assert.Equal(typeof(IReadOnlyList<TransmitTarget>), method!.ReturnType);
            Assert.Equal(
                new[] { typeof(IEnumerable<string>) },
                method.GetParameters().Select(p => p.ParameterType));
        }

        [Fact]
        public void ResolveAll_PreservesOrder_AndSkipsUnresolvableChannels()
        {
            var resolver = new TransmitTargetResolver(MakeCodeplug());
            var method = typeof(TransmitTargetResolver)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == "ResolveAll");
            Assert.NotNull(method);

            var targets = Assert.IsAssignableFrom<IReadOnlyList<TransmitTarget>>(
                method!.Invoke(
                    resolver,
                    new object?[] { new[] { "CH 2 P25", "No Such Channel", "CH 1 DMR" } }));

            Assert.Equal(2, targets.Count);
            Assert.Equal("31002", targets[0].TalkgroupId);
            Assert.Equal("31001", targets[1].TalkgroupId);
        }

        [Fact]
        public void ResolveAll_NullBlankUnknownAndEmptyInput_ReturnEmpty_NoThrow()
        {
            var resolver = new TransmitTargetResolver(MakeCodeplug());
            var method = typeof(TransmitTargetResolver)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == "ResolveAll");
            Assert.NotNull(method);

            var targets = Assert.IsAssignableFrom<IReadOnlyList<TransmitTarget>>(
                method!.Invoke(
                    resolver,
                    new object?[] { new string?[] { null, "", "   ", "No Such Channel" } }));
            var nullInput = Assert.IsAssignableFrom<IReadOnlyList<TransmitTarget>>(
                method.Invoke(resolver, new object?[] { null }));

            Assert.Empty(targets);
            Assert.Empty(nullInput);
        }
    }
}
