// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the zone/channel UI slice (plan Task 11
* remaining console views; audit deleg_9f6f3919 READY):
*
*   DvmConsole.Avalonia.ViewModels.ZoneViewModel (NEW)
*   ChannelSlotViewModel.Reassign (internal; public surface stays
*       byte-identical — shape gate locks Talkgroup/Status CanWrite=false)
*   MainWindowViewModel.Zones / SelectedZone (retained codeplug zones,
*       default = first zone, switch re-assigns slots and RESETS
*       selection/primary — selection is slot-scoped in the desk model)
*
* WPF parity: zones are TABS (one TabItem per Codeplug.Zones entry,
* dvmconsole/MainWindow.xaml.cs:379-469); each zone's channels render
* as ChannelBox cards with channelName (systemName) + Tgid display
* (ChannelBox.xaml:31-68). The Avalonia desk exposes every channel from
* the SELECTED zone in codeplug order. Talkgroup display = channel.Tgid,
* "NO TALKGROUP" when blank. The parameterless dashboard retains its four
* compatibility slots.
*/
using System;
using System.Collections.Generic;
using System.ComponentModel;
using dvmconsole;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for the zone/channel UI slice.
    /// </summary>
    public sealed class ZoneUiSliceTests
    {
        /* ------------------------------------------------------------------
        ** Fixture
        ** ---------------------------------------------------------------- */

        private static Codeplug MakeCodeplug()
        {
            return new Codeplug
            {
                Systems = new List<Codeplug.System>
                {
                    new Codeplug.System { Name = "Repeater 1", Rid = "1000001" },
                },
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
                            new Codeplug.Channel { Name = "CH 5 Extra", System = "Repeater 1", Tgid = "31005", Slot = 1, Mode = "dmr" },
                        },
                    },
                    new Codeplug.Zone
                    {
                        Name = "Zone B",
                        Channels = new List<Codeplug.Channel>
                        {
                            new Codeplug.Channel { Name = "B1", System = "Repeater 1", Tgid = "32001", Slot = 1, Mode = "dmr" },
                            new Codeplug.Channel { Name = "B2", System = "Repeater 1", Tgid = "32002", Slot = 2, Mode = "dmr" },
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
        ** ZoneViewModel surface
        ** ---------------------------------------------------------------- */

        [Fact]
        public void ZoneViewModel_ExactSurface()
        {
            var type = typeof(ZoneViewModel);
            Assert.True(type.IsSealed);
            var ctor = type.GetConstructor(new[]
            {
                typeof(string), typeof(IReadOnlyList<Codeplug.Channel>),
            });
            Assert.NotNull(ctor);
            Assert.Equal(typeof(string), type.GetProperty(nameof(ZoneViewModel.Name))!.PropertyType);
            Assert.Equal(
                typeof(IReadOnlyList<Codeplug.Channel>),
                type.GetProperty(nameof(ZoneViewModel.Channels))!.PropertyType);
            Assert.False(type.GetProperty(nameof(ZoneViewModel.Name))!.CanWrite);
            Assert.False(type.GetProperty(nameof(ZoneViewModel.Channels))!.CanWrite);
        }

        [Fact]
        public void ZoneViewModel_StoresNameAndChannels()
        {
            var zone = MakeCodeplug().Zones[0];
            var vm = new ZoneViewModel(zone.Name, zone.Channels!);

            Assert.Equal("Zone A", vm.Name);
            Assert.Same(zone.Channels, vm.Channels);
        }

        /* ------------------------------------------------------------------
        ** Zones exposure and default selection
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Zones_ExposedInCodeplugOrder_WithChannelCounts()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            Assert.Equal(3, vm.Zones.Count);
            Assert.Equal("Zone A", vm.Zones[0].Name);
            Assert.Equal(5, vm.Zones[0].Channels!.Count);
            Assert.Equal("Zone B", vm.Zones[1].Name);
            Assert.Equal(2, vm.Zones[1].Channels!.Count);
            Assert.Equal("Zone C (empty)", vm.Zones[2].Name);
            Assert.Null(vm.Zones[2].Channels);
        }

        [Fact]
        public void NullCodeplug_NoZones_AllSlotsUnassigned()
        {
            var vm = new MainWindowViewModel(null, null, null, null, null, null);

            Assert.Empty(vm.Zones);
            Assert.Null(vm.SelectedZone);
            Assert.All(vm.Channels, c => Assert.Null(c.ChannelName));
            Assert.All(vm.Channels, c => Assert.Equal("NO TALKGROUP", c.Talkgroup));
        }

        [Fact]
        public void DefaultSelectedZone_IsFirstZone()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            Assert.NotNull(vm.SelectedZone);
            Assert.Equal("Zone A", vm.SelectedZone!.Name);
        }

        /* ------------------------------------------------------------------
        ** Slot assignment from the selected zone
        ** ---------------------------------------------------------------- */

        [Fact]
        public void DefaultZone_ResourcesAssignedFromEveryChannel()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            Assert.Equal("CH 1 DMR", vm.Channels[0].ChannelName);
            Assert.Equal("31001", vm.Channels[0].Talkgroup);
            Assert.Equal("CH 2 P25", vm.Channels[1].ChannelName);
            Assert.Equal("31002", vm.Channels[1].Talkgroup);
            Assert.Equal("CH 3", vm.Channels[2].ChannelName);
            Assert.Equal("CH 4", vm.Channels[3].ChannelName);
            Assert.Equal("CH 5 Extra", vm.Channels[4].ChannelName);
        }

        [Fact]
        public void ZoneWithFewerThanFourChannels_ExposesExactCollection()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            vm.SelectedZone = vm.Zones[1]; // Zone B: 2 channels

            Assert.Equal("B1", vm.Channels[0].ChannelName);
            Assert.Equal("32001", vm.Channels[0].Talkgroup);
            Assert.Equal("B2", vm.Channels[1].ChannelName);
            Assert.Equal("32002", vm.Channels[1].Talkgroup);
            Assert.Equal(2, vm.Channels.Count);
        }

        [Fact]
        public void ZoneWithNullChannels_ExposesEmptyCollection_NoThrow()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            vm.SelectedZone = vm.Zones[2]; // Zone C: null Channels

            Assert.Empty(vm.Channels);
        }

        /* ------------------------------------------------------------------
        ** Zone switching semantics
        ** ---------------------------------------------------------------- */

        [Fact]
        public void ZoneSwitch_ReassignsSlots_AndResetsSelectionAndPrimary()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            // Select + promote slot 1 in Zone A.
            vm.ProcessChannelClick(1, setPrimary: false);
            vm.ProcessChannelClick(1, setPrimary: true);
            Assert.NotNull(vm.PrimaryChannel);
            Assert.True(vm.Channels[0].IsSelected);

            // Switch to Zone B: slots re-assign, selection and primary reset.
            vm.SelectedZone = vm.Zones[1];

            Assert.Equal("B1", vm.Channels[0].ChannelName);
            Assert.Null(vm.PrimaryChannel);
            Assert.All(vm.Channels, c => Assert.False(c.IsSelected));
            Assert.All(vm.Channels, c => Assert.False(c.IsPrimary));
        }

        [Fact]
        public void SelectedZone_NotificationOccursAfterStateIsConsistent()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());
            vm.ProcessChannelClick(1, setPrimary: false);
            vm.ProcessChannelClick(1, setPrimary: true);

            var notificationCount = 0;
            vm.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName != nameof(MainWindowViewModel.SelectedZone))
                {
                    return;
                }

                notificationCount++;
                Assert.Same(vm.Zones[1], vm.SelectedZone);
                Assert.Null(vm.PrimaryChannel);
                Assert.Empty(vm.SelectedChannels);
                Assert.Equal("B1", vm.Channels[0].ChannelName);
                Assert.Equal("B2", vm.Channels[1].ChannelName);
                Assert.All(vm.Channels, channel => Assert.False(channel.IsSelected));
                Assert.All(vm.Channels, channel => Assert.False(channel.IsPrimary));
            };

            vm.SelectedZone = vm.Zones[1];

            Assert.Equal(1, notificationCount);
        }

        [Fact]
        public void SameZoneReselect_NoOp_ZeroNotifications()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            int zoneChanges = 0;
            int slotChanges = 0;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.SelectedZone)) zoneChanges++;
                if (e.PropertyName == nameof(MainWindowViewModel.Channels)) slotChanges++;
            };

            vm.SelectedZone = vm.Zones[0]; // same instance already selected
            Assert.Equal(0, zoneChanges);
            Assert.Equal(0, slotChanges);
            Assert.Equal("CH 1 DMR", vm.Channels[0].ChannelName); // untouched
        }

        [Fact]
        public void ForeignZone_Rejected()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());
            var foreign = new ZoneViewModel("Not Mine", new List<Codeplug.Channel>());

            vm.SelectedZone = foreign;

            Assert.Equal("Zone A", vm.SelectedZone!.Name); // unchanged
        }

        [Fact]
        public void NullSelectedZone_Rejected_WhenZonesExist()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            vm.SelectedZone = null;

            Assert.NotNull(vm.SelectedZone); // null rejected while zones exist
            Assert.Equal("Zone A", vm.SelectedZone!.Name);
        }

        /* ------------------------------------------------------------------
        ** Reassign notification discipline (change-only)
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Reassign_ChangeOnlyNotifications()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");
            var seen = new List<string>();
            ((INotifyPropertyChanged)slot).PropertyChanged += (s, e) => seen.Add(e.PropertyName!);

            slot.Reassign("CH 1 DMR", "31001");
            Assert.Equal(new[] { nameof(ChannelSlotViewModel.ChannelName), nameof(ChannelSlotViewModel.Talkgroup) }, seen);

            // Same values: no notifications.
            seen.Clear();
            slot.Reassign("CH 1 DMR", "31001");
            Assert.Empty(seen);
        }

        [Fact]
        public void Reassign_BlankTalkgroup_ShowsNoTalkgroup()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");

            slot.Reassign("CH 1 DMR", null);
            Assert.Equal("NO TALKGROUP", slot.Talkgroup);
            slot.Reassign("CH 1 DMR", "   ");
            Assert.Equal("NO TALKGROUP", slot.Talkgroup);
        }

        /* ------------------------------------------------------------------
        ** PTT flow after a zone switch (resolver-visible name)
        ** ---------------------------------------------------------------- */

        [Fact]
        public void PttFlow_PostZoneSwitch_ResolverVisibleName()
        {
            var vm = new MainWindowViewModel(MakeCodeplug().Systems, null, null, null, null, MakeCodeplug());

            vm.SelectedZone = vm.Zones[1]; // Zone B
            vm.ProcessChannelClick(1, setPrimary: false);
            vm.ProcessChannelClick(1, setPrimary: true);

            Assert.NotNull(vm.PrimaryChannel);
            Assert.Equal("B1", vm.PrimaryChannel!.ChannelName);
            Assert.Equal("32001", vm.PrimaryChannel!.Talkgroup);
        }
    }
}
