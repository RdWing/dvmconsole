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
    /// RED contract for headless TAR shell composition: the dashboard view-model
    /// composes the TAR configuration surface only with a codeplug and persistence,
    /// loads legacy WPF keys, and persists the configuration save event.
    /// </summary>
    public sealed class MainWindowTarCompositionTests
    {
        [Fact]
        public void ComposesTarConfiguration_LoadsLegacyTalkgroupKey_AndPersistsSave()
        {
            string filePath = TemporarySettingsPath();
            try
            {
                var persistence = new TarSettingsPersistence(new SettingsSectionStore(filePath));
                persistence.Save(
                    "  /tmp/tar-shell  ",
                    new Dictionary<string, TarChannelConfig>
                    {
                        ["77"] = new TarChannelConfig
                        {
                            Enabled = true,
                            RetentionDays = 14,
                            IgnoredSubscriberIds = new List<uint> { 2, 9 },
                        },
                    });
                var codeplug = MakeCodeplug();

                var viewModel = new MainWindowViewModel(
                    codeplug.Systems,
                    catalog: null,
                    hotkeys: null,
                    persistence: null,
                    vocoderStatus: null,
                    codeplug: codeplug,
                    callHistory: null,
                    tarPersistence: persistence);

                Assert.NotNull(viewModel.TarConfiguration);
                TarConfigurationViewModel configuration = viewModel.TarConfiguration!;
                var item = Assert.Single(Assert.Single(configuration.ZoneGroups).Channels);
                Assert.Equal("sys|77", item.ResourceKey);
                Assert.True(item.Enabled);
                Assert.Equal(14, item.RetentionDays);

                item.RetentionDays = 21;
                Assert.True(configuration.Save());
                Assert.Equal("TAR settings saved.", viewModel.TarSaveFeedback);

                Assert.True(persistence.TryLoad(out UserSettingsTarSection saved));
                var savedConfig = Assert.Single(saved.TarChannelConfigs);
                Assert.Equal("sys|77", savedConfig.Key);
                Assert.Equal(21, savedConfig.Value.RetentionDays);
                Assert.Equal(new uint[] { 2, 9 }, savedConfig.Value.IgnoredSubscriberIds);
            }
            finally
            {
                DeleteSettingsFile(filePath);
            }
        }

        [Fact]
        public void MissingCodeplugOrPersistence_DoesNotComposeTarConfiguration()
        {
            string filePath = TemporarySettingsPath();
            try
            {
                var persistence = new TarSettingsPersistence(new SettingsSectionStore(filePath));
                var withoutCodeplug = new MainWindowViewModel(
                    systems: null,
                    catalog: null,
                    hotkeys: null,
                    persistence: null,
                    vocoderStatus: null,
                    codeplug: null,
                    callHistory: null,
                    tarPersistence: persistence);
                var withoutPersistence = new MainWindowViewModel(
                    systems: null,
                    catalog: null,
                    hotkeys: null,
                    persistence: null,
                    vocoderStatus: null,
                    codeplug: MakeCodeplug(),
                    callHistory: null,
                    tarPersistence: null);

                Assert.Null(withoutCodeplug.TarConfiguration);
                Assert.Null(withoutPersistence.TarConfiguration);
                Assert.Empty(withoutCodeplug.TarSaveFeedback);
                Assert.Empty(withoutPersistence.TarSaveFeedback);
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
                                Name = "Channel Name",
                                System = "SYS",
                                Tgid = "77",
                                Mode = "dmr",
                            },
                        },
                    },
                },
            };

        private static string TemporarySettingsPath()
            => Path.Combine(Path.GetTempPath(), "dvmconsole-tar-shell-" + Guid.NewGuid().ToString("N") + ".json");

        private static void DeleteSettingsFile(string filePath)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }
}
