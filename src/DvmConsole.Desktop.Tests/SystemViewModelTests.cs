using DvmConsole.Core.Settings;
using DvmConsole.Core.Runtime;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using System.Globalization;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class SystemViewModelTests
{
    [Fact]
    public async Task InvalidCodeplugReportsFailureWithoutReplacingLastKnownPath()
    {
        string settingsPath = CreateSettingsPath();
        string invalidPath = Path.Combine(Path.GetTempPath(), $"dvmconsole-invalid-{Guid.NewGuid():N}.yml");
        string knownPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        var store = new UserSettingsStore(settingsPath);

        try
        {
            store.Save(new UserSettings { LastCodeplugPath = knownPath });
            await File.WriteAllTextAsync(invalidPath, "systems:\n  - name: broken\n    address: [\n");

            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(invalidPath, store);

            Assert.False(viewModel.IsCodeplugLoaded);
            Assert.StartsWith("Unable to load codeplug:", viewModel.StatusText);
            Assert.Equal(knownPath, store.Load().LastCodeplugPath);
        }
        finally
        {
            File.Delete(invalidPath);
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task LoadsVariableSystemTabsFromCodeplugSystems()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(path, new UserSettingsStore(settingsPath));

            Assert.Equal(["Alpha", "Beta"], viewModel.Systems.Select(system => system.Name));
            Assert.Equal(2, viewModel.Systems.Count);
            Assert.Equal(
                ["Alpha Dispatch", "Alpha Operations", "Alpha Emergency"],
                viewModel.Systems[0].Channels.Select(channel => channel.Name));
            Assert.Equal(["DMR", "P25", "DMR"], viewModel.Systems[0].Channels.Select(channel => channel.ModeText));
            Assert.Equal([101u, 102u, 103u], viewModel.Systems[0].Channels.Select(channel => channel.Definition.DestinationId));
            Assert.Equal(["Beta Dispatch", "Beta Operations"], viewModel.Systems[1].Channels.Select(channel => channel.Name));
            Assert.Equal(["P25", "DMR"], viewModel.Systems[1].Channels.Select(channel => channel.ModeText));
            Assert.Equal([201u, 202u], viewModel.Systems[1].Channels.Select(channel => channel.Definition.DestinationId));
            Assert.Equal(["Dispatch", "Operations"], viewModel.Systems[0].Zones.Select(zone => zone.Name));
            Assert.Equal(["Alpha Dispatch", "Alpha Operations"], viewModel.Systems[0].Zones[0].Channels.Select(channel => channel.Name));
            Assert.Equal(["Alpha Emergency"], viewModel.Systems[0].Zones[1].Channels.Select(channel => channel.Name));
            Assert.Equal(["Dispatch", "Operations"], viewModel.Systems[1].Zones.Select(zone => zone.Name));
            Assert.Equal("TG 101", viewModel.Systems[0].Zones[0].Channels[0].TalkgroupText);
            Assert.Single(viewModel.PatchGroups);
            Assert.Equal("Dispatch Patch", viewModel.PatchGroups[0].Name);
            Assert.Equal(5, viewModel.PatchGroups[0].Members.Count);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task RecordsOneHistoryEntryPerNewVoiceStream()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(path, new UserSettingsStore(settingsPath));
            SystemViewModel system = viewModel.Systems[0];

            viewModel.ProcessTraffic(system, new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                42,
                101,
                0,
                "GROUP",
                "VOICE",
                "VOICE",
                1,
                77,
                new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes]));
            viewModel.ProcessTraffic(system, new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                42,
                101,
                0,
                "GROUP",
                "VOICE",
                "VOICE",
                2,
                77,
                new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes]));
            viewModel.ProcessTraffic(system, new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                42,
                101,
                0,
                "GROUP",
                "VOICE",
                "VOICE",
                3,
                78,
                new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes]));

            Assert.Equal(2, viewModel.CallHistory.Count);
            Assert.Equal((uint)78, viewModel.CallHistory[0].StreamId);
            Assert.Equal((uint)77, viewModel.CallHistory[1].StreamId);
            Assert.Equal("Alpha Dispatch", viewModel.CallHistory[1].ChannelName);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task PersistsAndRestoresTheSelectedChannel()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = Path.Combine(Path.GetTempPath(), "dvmconsole-settings-tests", $"{Guid.NewGuid():N}", "UserSettings.json");
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using (MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store))
            {
                viewModel.SelectChannel(viewModel.Systems[1].Channels[1]);
            }

            Assert.Equal("Beta\u001FBeta Operations", store.Load().LastSelectedChannelKey);

            await using MainWindowViewModel restored = MainWindowViewModel.Load(codeplugPath, store);
            Assert.Equal("Beta Operations", restored.SelectedChannel?.Name);
            Assert.Equal("Beta", restored.SelectedSystem?.Name);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task LaunchWithoutAPathRestoresTheLastCodeplug()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = Path.Combine(Path.GetTempPath(), "dvmconsole-settings-tests", $"{Guid.NewGuid():N}", "UserSettings.json");
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings { LastCodeplugPath = codeplugPath });

        try
        {
            await using MainWindowViewModel restored = MainWindowViewModel.Load(null, store);

            Assert.Equal(2, restored.Systems.Count);
            Assert.Equal(["Alpha", "Beta"], restored.Systems.Select(system => system.Name));
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task SavesUsesAndDeletesMicrophonePresets()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            viewModel.TalkPermitTone = true;
            Assert.True(store.Load().TalkPermitTone);
            viewModel.DarkMode = true;
            Assert.True(store.Load().DarkMode);
            viewModel.AudioInputPresetNameText = " Field ";
            viewModel.AudioInputGainText = "1.25";
            viewModel.AudioInputLowGainText = "-2";
            viewModel.AudioInputMidGainText = "1";
            viewModel.AudioInputHighGainText = "3";

            viewModel.SaveAudioInputPreset();

            AudioInputPresetViewModel preset = Assert.Single(viewModel.AudioInputPresets);
            Assert.Equal("Field", preset.Name);
            Assert.Equal("Field", store.Load().AudioInputPresetName);
            Assert.Equal(1.25, store.Load().AudioInputPresets[0].Gain);

            viewModel.AudioInputGainText = "2";
            viewModel.UseAudioInputPreset(preset);
            Assert.Equal("1.25", viewModel.AudioInputGainText);
            Assert.Equal("-2", viewModel.AudioInputLowGainText);

            viewModel.DeleteAudioInputPreset(preset);
            Assert.Empty(viewModel.AudioInputPresets);
            Assert.Empty(store.Load().AudioInputPresets);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task PersistsAndFormatsToolbarClockSettings()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);

            Assert.True(viewModel.ClockUse24HourTime);
            Assert.True(viewModel.ClockShowSeconds);
            Assert.Equal("13:05:09", MainWindowViewModel.FormatClock(
                new DateTime(2026, 8, 14, 13, 5, 9),
                use24HourTime: true,
                showSeconds: true));
            Assert.Equal(new DateTime(2026, 8, 14, 13, 5, 9).ToString("h:mm tt", CultureInfo.CurrentCulture), MainWindowViewModel.FormatClock(
                new DateTime(2026, 8, 14, 13, 5, 9),
                use24HourTime: false,
                showSeconds: false));

            viewModel.ClockUse24HourTime = false;
            viewModel.ClockShowSeconds = false;
            viewModel.KeepWindowOnTop = true;
            viewModel.TogglePttMode = true;

            UserSettings saved = store.Load();
            Assert.False(saved.ClockUse24HourTime);
            Assert.False(saved.ClockShowSeconds);
            Assert.True(saved.KeepWindowOnTop);
            Assert.True(saved.TogglePttMode);
            Assert.NotEmpty(viewModel.ClockText);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task RestoresOnlyEnabledPatchStateForConfiguredPatchGroups()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings
        {
            RetainPatchStateOnStartup = true,
            PatchGroupMemberships = new Dictionary<string, List<PatchMemberSetting>>
            {
                ["Dispatch Patch"] =
                [
                    new PatchMemberSetting { SystemName = "Alpha", DestinationId = 101 },
                    new PatchMemberSetting { SystemName = "Beta", DestinationId = 201 }
                ],
                ["Operations Select"] =
                [
                    new PatchMemberSetting { SystemName = "Alpha", DestinationId = 103 },
                    new PatchMemberSetting { SystemName = "Beta", DestinationId = 202 }
                ]
            },
            PatchGroupEnabledStates = new Dictionary<string, bool>
            {
                ["Dispatch Patch"] = true,
                ["Operations Select"] = true
            }
        });

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);

            Assert.Equal(["Dispatch Patch"], viewModel.PatchGroupNames);
            PatchGroupEditorViewModel group = Assert.Single(viewModel.PatchGroups);
            Assert.True(group.IsEnabled);
            Assert.Equal(
                ["Alpha Dispatch", "Beta Dispatch"],
                group.Members.Where(member => member.IsMember).Select(member => member.Channel.Name));
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task PersistsIgnoredRecordingSubscribersForTheSelectedChannel()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            ChannelViewModel channel = viewModel.Systems[0].Channels[0];

            viewModel.SetRecordingIgnoredSubscribers(channel, [1001, 42, 1001, 0]);

            Assert.Equal([42u, 1001u], store.Load().RecordingIgnoredSubscriberIds[channel.SettingsKey]);
            Assert.Equal("42, 1001", channel.IgnoredSubscriberIdsText);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    private static string CreateSettingsPath()
        => Path.Combine(Path.GetTempPath(), "dvmconsole-settings-tests", $"{Guid.NewGuid():N}", "UserSettings.json");

    private static void CleanupSettingsPath(string settingsPath)
    {
        string? directory = Path.GetDirectoryName(settingsPath);
        if (File.Exists(settingsPath))
            File.Delete(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
