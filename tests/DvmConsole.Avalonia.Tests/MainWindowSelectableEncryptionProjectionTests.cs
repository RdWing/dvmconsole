// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;
using DvmConsole.Avalonia.ViewModels;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for projecting WPF selectable-encryption eligibility from
    /// codeplug mode, selectable flag, and configured key material.
    /// </summary>
    public sealed class MainWindowSelectableEncryptionProjectionTests
    {
        [Fact]
        public void ReassignSlots_ProjectsOnlyP25ChannelsWithSelectableConfiguredEncryption()
        {
            var codeplug = MakeCodeplug(
                new Codeplug.Zone
                {
                    Name = "Zone A",
                    Channels = new List<Codeplug.Channel>
                    {
                        new()
                        {
                            Name = "P25 Secure",
                            System = "System A",
                            Tgid = "100",
                            Mode = "p25",
                            SelectableEncryption = true,
                            Algo = "aes",
                            KeyId = "01",
                        },
                        new()
                        {
                            Name = "DMR Secure",
                            System = "System A",
                            Tgid = "101",
                            Mode = "dmr",
                            SelectableEncryption = true,
                            Algo = "aes",
                            KeyId = "01",
                        },
                        new()
                        {
                            Name = "P25 Missing Key",
                            System = "System A",
                            Tgid = "102",
                            Mode = "p25",
                            SelectableEncryption = true,
                            Algo = "aes",
                        },
                        new()
                        {
                            Name = "P25 Not Selectable",
                            System = "System A",
                            Tgid = "103",
                            Mode = "p25",
                            SelectableEncryption = false,
                            Algo = "aes",
                            KeyId = "01",
                        },
                    },
                });

            var vm = new MainWindowViewModel(
                null,
                null,
                null,
                null,
                null,
                codeplug);

            Assert.True(vm.Channels[0].IsEncryptionSelectable);
            Assert.False(vm.Channels[1].IsEncryptionSelectable);
            Assert.False(vm.Channels[2].IsEncryptionSelectable);
            Assert.False(vm.Channels[3].IsEncryptionSelectable);
        }

        [Fact]
        public void ZoneSwitch_ReprojectsFreshSelectableEncryptionState()
        {
            var codeplug = MakeCodeplug(
                new Codeplug.Zone
                {
                    Name = "P25 Zone",
                    Channels = new List<Codeplug.Channel>
                    {
                        new()
                        {
                            Name = "P25 Secure",
                            System = "System A",
                            Tgid = "200",
                            Mode = "P25",
                            SelectableEncryption = true,
                            Algo = "des",
                            KeyId = "02",
                        },
                    },
                },
                new Codeplug.Zone
                {
                    Name = "DMR Zone",
                    Channels = new List<Codeplug.Channel>
                    {
                        new()
                        {
                            Name = "DMR Secure",
                            System = "System A",
                            Tgid = "201",
                            Mode = "DMR",
                            SelectableEncryption = true,
                            Algo = "des",
                            KeyId = "02",
                        },
                    },
                });

            var vm = new MainWindowViewModel(null, null, null, null, null, codeplug);
            Assert.True(vm.Channels[0].IsEncryptionSelectable);

            vm.SelectedZone = vm.Zones[1];

            Assert.Single(vm.Channels);
            Assert.False(vm.Channels[0].IsEncryptionSelectable);
        }

        [Fact]
        public void CompatibilitySlots_RemainNotSelectableWithoutCodeplugAssignment()
        {
            var vm = new MainWindowViewModel();

            Assert.Equal(4, vm.Channels.Count);
            Assert.All(vm.Channels, slot => Assert.False(slot.IsEncryptionSelectable));
        }

        private static Codeplug MakeCodeplug(params Codeplug.Zone[] zones)
            => new()
            {
                Systems = new List<Codeplug.System>
                {
                    new() { Name = "System A", Rid = "1000001" },
                },
                Zones = new List<Codeplug.Zone>(zones),
            };
    }
}
