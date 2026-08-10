// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using dvmconsole;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for projecting the composed TAR configuration into the
    /// dashboard's fixed channel-slot indicator state.
    /// </summary>
    public sealed class MainWindowTarIndicatorProjectionTests
    {
        [Fact]
        public void ComposedTarConfiguration_ProjectsPersistedEnabledStateIntoSelectedSlot()
        {
            string filePath = TemporarySettingsPath();
            try
            {
                var persistence = new TarSettingsPersistence(new SettingsSectionStore(filePath));
                persistence.Save(
                    "/tmp/tar-indicator",
                    new Dictionary<string, TarChannelConfig>
                    {
                        ["sys2|88"] = new TarChannelConfig { Enabled = true },
                    });

                var viewModel = new MainWindowViewModel(
                    systems: null,
                    catalog: null,
                    hotkeys: null,
                    persistence: null,
                    vocoderStatus: null,
                    codeplug: MakeCodeplug(),
                    callHistory: null,
                    tarPersistence: persistence);

                Assert.False(viewModel.Channels[0].TarRecordingEnabled);
                Assert.True(viewModel.Channels[1].TarRecordingEnabled);
            }
            finally
            {
                DeleteSettingsFile(filePath);
            }
        }

        [Fact]
        public void TarConfigurationEnabledChange_RefreshesMatchingSelectedSlot()
        {
            string filePath = TemporarySettingsPath();
            try
            {
                var persistence = new TarSettingsPersistence(new SettingsSectionStore(filePath));
                var viewModel = new MainWindowViewModel(
                    systems: null,
                    catalog: null,
                    hotkeys: null,
                    persistence: null,
                    vocoderStatus: null,
                    codeplug: MakeCodeplug(),
                    callHistory: null,
                    tarPersistence: persistence);

                Assert.NotNull(viewModel.TarConfiguration);
                TarConfigurationViewModel configuration = viewModel.TarConfiguration!;
                var item = configuration.ZoneGroups[0].Channels[0];
                Assert.False(viewModel.Channels[0].TarRecordingEnabled);

                item.Enabled = true;

                Assert.True(viewModel.Channels[0].TarRecordingEnabled);
            }
            finally
            {
                DeleteSettingsFile(filePath);
            }
        }

        [Fact]
        public void SelectedZoneChange_ReprojectsIndicatorsForNewZone()
        {
            string filePath = TemporarySettingsPath();
            try
            {
                var persistence = new TarSettingsPersistence(new SettingsSectionStore(filePath));
                persistence.Save(
                    "/tmp/tar-indicator",
                    new Dictionary<string, TarChannelConfig>
                    {
                        ["other|99"] = new TarChannelConfig { Enabled = true },
                    });

                var viewModel = new MainWindowViewModel(
                    systems: null,
                    catalog: null,
                    hotkeys: null,
                    persistence: null,
                    vocoderStatus: null,
                    codeplug: MakeTwoZoneCodeplug(),
                    callHistory: null,
                    tarPersistence: persistence);

                Assert.False(viewModel.Channels[0].TarRecordingEnabled);

                viewModel.SelectedZone = viewModel.Zones[1];

                Assert.True(viewModel.Channels[0].TarRecordingEnabled);
            }
            finally
            {
                DeleteSettingsFile(filePath);
            }
        }

        private static Codeplug MakeCodeplug()
            => new Codeplug
            {
                Systems = new List<Codeplug.System>(),
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone
                    {
                        Name = "Zone",
                        Channels = new List<Codeplug.Channel>
                        {
                            new Codeplug.Channel
                            {
                                Name = "Common",
                                System = "SYS",
                                Tgid = "77",
                                Mode = "dmr",
                            },
                            new Codeplug.Channel
                            {
                                Name = "Common",
                                System = "SYS2",
                                Tgid = "88",
                                Mode = "dmr",
                            },
                        },
                    },
                },
            };

        private static Codeplug MakeTwoZoneCodeplug()
            => new Codeplug
            {
                Systems = new List<Codeplug.System>(),
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone
                    {
                        Name = "First",
                        Channels = new List<Codeplug.Channel>
                        {
                            new Codeplug.Channel
                            {
                                Name = "First channel",
                                System = "SYS",
                                Tgid = "77",
                                Mode = "dmr",
                            },
                        },
                    },
                    new Codeplug.Zone
                    {
                        Name = "Second",
                        Channels = new List<Codeplug.Channel>
                        {
                            new Codeplug.Channel
                            {
                                Name = "Second channel",
                                System = "OTHER",
                                Tgid = "99",
                                Mode = "dmr",
                            },
                        },
                    },
                },
            };

        private static string TemporarySettingsPath()
            => Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-tar-indicator-" + Guid.NewGuid().ToString("N") + ".json");

        private static void DeleteSettingsFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
