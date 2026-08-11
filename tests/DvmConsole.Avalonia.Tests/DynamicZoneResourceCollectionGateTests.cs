// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using dvmconsole;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Gate 2.1 RED contract for replacing the four-slot dashboard
    /// projection with the selected zone's complete resource collection.
    /// The existing Channels property remains the compatibility binding
    /// consumed by the current shell until later card migration gates.
    /// </summary>
    public sealed class DynamicZoneResourceCollectionGateTests
    {
        [Fact]
        public void EmptyZone_ExposesNoResources_NoFillerSlots()
        {
            var vm = CreateVm(Zone("Empty"));

            Assert.Empty(vm.Channels);
        }

        [Fact]
        public void EveryZoneChannelCountFromZeroThrough256_IsRepresentedExactly()
        {
            for (var count = 0; count <= 256; count++)
            {
                var channels = Enumerable.Range(1, count)
                    .Select(i => Channel($"CH {i}", "System A", (1000 + i).ToString()))
                    .ToArray();
                var vm = CreateVm(Zone($"Count {count}", channels));

                Assert.Equal(count, vm.Channels.Count);
                Assert.Equal(
                    Enumerable.Range(1, count).Select(i => $"CH {i}"),
                    vm.Channels.Select(channel => channel.ChannelName));
                Assert.Equal(
                    Enumerable.Range(1, count),
                    vm.Channels.Select(channel => channel.Number));

                if (count > 0)
                {
                    Assert.Equal("CHANNEL 01", vm.Channels[0].Label);
                    Assert.Equal($"CHANNEL {count:00}", vm.Channels[count - 1].Label);
                }
            }
        }

        [Fact]
        public void OneChannelZone_PreservesCodeplugOrderAndIdentity()
        {
            var vm = CreateVm(
                Zone(
                    "One",
                    Channel("Dispatch", "System A", "101")));

            var resource = Assert.Single(vm.Channels);

            Assert.Equal(1, resource.Number);
            Assert.Equal("CHANNEL 01", resource.Label);
            Assert.Equal("Dispatch", resource.ChannelName);
            Assert.Equal("101", resource.Talkgroup);
            Assert.Equal("system a|101", ResourceKey(resource));
        }

        [Fact]
        public void FourChannelZone_PreservesEveryResourceInCodeplugOrder()
        {
            var channels = Enumerable.Range(1, 4)
                .Select(i => Channel($"CH {i}", "System A", (100 + i).ToString()))
                .ToArray();
            var vm = CreateVm(Zone("Four", channels));

            Assert.Equal(
                new[] { "CH 1", "CH 2", "CH 3", "CH 4" },
                vm.Channels.Select(channel => channel.ChannelName).ToArray());
            Assert.Equal(new[] { 1, 2, 3, 4 }, vm.Channels.Select(channel => channel.Number));
        }

        [Fact]
        public void MoreThanFourChannels_PreservesAllResourcesWithoutTruncationOrFillers()
        {
            var channels = Enumerable.Range(1, 6)
                .Select(i => Channel($"CH {i}", "System A", (200 + i).ToString()))
                .ToArray();
            var vm = CreateVm(Zone("Large", channels));

            Assert.Equal(6, vm.Channels.Count);
            Assert.Equal(
                new[] { "CH 1", "CH 2", "CH 3", "CH 4", "CH 5", "CH 6" },
                vm.Channels.Select(channel => channel.ChannelName).ToArray());
            Assert.Equal(
                new[] { 1, 2, 3, 4, 5, 6 },
                vm.Channels.Select(channel => channel.Number).ToArray());
            Assert.DoesNotContain(vm.Channels, channel => channel.ChannelName is null);
        }

        [Fact]
        public void DynamicResourceBeyondCompatibilityRange_CanBeSelected()
        {
            var channels = Enumerable.Range(1, 6)
                .Select(i => Channel($"CH {i}", "System A", (250 + i).ToString()))
                .ToArray();
            var vm = CreateVm(Zone("Large", channels));

            vm.ProcessChannelClick(5, setPrimary: false);

            Assert.True(vm.Channels[4].IsSelected);
            Assert.Same(vm.Channels[4], Assert.Single(vm.SelectedChannels));
        }

        [Fact]
        public void DuplicateChannelNamesAcrossSystems_KeepDistinctStableResourceKeys()
        {
            var vm = CreateVm(
                Zone(
                    "Duplicates",
                    Channel("Dispatch", "System A", "301"),
                    Channel("Dispatch", "System B", "301")));

            Assert.Equal("system a|301", ResourceKey(vm.Channels[0]));
            Assert.Equal("system b|301", ResourceKey(vm.Channels[1]));
            Assert.NotEqual(ResourceKey(vm.Channels[0]), ResourceKey(vm.Channels[1]));
        }

        [Fact]
        public void SwitchingZones_RebuildsCompleteCollectionAndResetsSelection()
        {
            var first = Zone(
                "First",
                Channel("A1", "System A", "401"),
                Channel("A2", "System A", "402"),
                Channel("A3", "System A", "403"),
                Channel("A4", "System A", "404"),
                Channel("A5", "System A", "405"));
            var second = Zone(
                "Second",
                Channel("B1", "System B", "501"));
            var vm = CreateVm(first, second);

            vm.ProcessChannelClick(vm.Channels[0].Number, setPrimary: false);
            Assert.Single(vm.SelectedChannels);

            vm.SelectedZone = vm.Zones[1];

            Assert.Equal("Second", vm.SelectedZone!.Name);
            Assert.Single(vm.Channels);
            Assert.Equal("B1", vm.Channels[0].ChannelName);
            Assert.Empty(vm.SelectedChannels);
            Assert.Null(vm.PrimaryChannel);
        }

        [Fact]
        public void SwitchingZones_NotifiesChannelsAfterRebuild()
        {
            var vm = CreateVm(
                Zone("First", Channel("A1", "System A", "451")),
                Zone("Second", Channel("B1", "System B", "551"), Channel("B2", "System B", "552")));
            var notifications = new List<string?>();
            vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

            vm.SelectedZone = vm.Zones[1];

            Assert.Contains(nameof(MainWindowViewModel.Channels), notifications);
            Assert.Contains(nameof(MainWindowViewModel.SelectedZone), notifications);
            var channelsIndex = notifications.IndexOf(nameof(MainWindowViewModel.Channels));
            var selectedZoneIndex = notifications.IndexOf(nameof(MainWindowViewModel.SelectedZone));
            Assert.True(channelsIndex > notifications.IndexOf(nameof(MainWindowViewModel.SelectedChannels)));
            Assert.True(channelsIndex < selectedZoneIndex);
            Assert.Equal(2, vm.Channels.Count);
            Assert.Equal("B2", vm.Channels[1].ChannelName);
        }

        [Fact]
        public void ResourceKey_IsPublicReadOnlyStringProperty()
        {
            var property = typeof(ChannelSlotViewModel).GetProperty("ResourceKey");

            Assert.NotNull(property);
            Assert.Equal(typeof(string), property!.PropertyType);
            Assert.False(property.CanWrite);
        }

        private static MainWindowViewModel CreateVm(params Codeplug.Zone[] zones)
        {
            var codeplug = new Codeplug
            {
                Systems = new List<Codeplug.System>(),
                Zones = zones.ToList()
            };

            return new MainWindowViewModel(
                systems: codeplug.Systems,
                catalog: null,
                hotkeys: null,
                persistence: null,
                vocoderStatus: null,
                codeplug: codeplug);
        }

        private static Codeplug.Zone Zone(string name, params Codeplug.Channel[] channels)
            => new Codeplug.Zone
            {
                Name = name,
                Channels = channels.ToList()
            };

        private static Codeplug.Channel Channel(string name, string system, string talkgroup)
            => new Codeplug.Channel
            {
                Name = name,
                System = system,
                Tgid = talkgroup,
                Mode = "dmr"
            };

        private static string? ResourceKey(ChannelSlotViewModel resource)
        {
            var property = typeof(ChannelSlotViewModel).GetProperty("ResourceKey");
            Assert.NotNull(property);
            return property!.GetValue(resource) as string;
        }
    }
}
