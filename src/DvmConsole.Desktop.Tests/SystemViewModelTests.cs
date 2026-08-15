using DvmConsole.Core.Settings;
using DvmConsole.Core.Runtime;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using System.Globalization;
using fnecore.DMR;
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
    public async Task MissingEncryptionKeyFileDoesNotRejectOtherwiseValidCodeplug()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dvmconsole-codeplug-tests", Guid.NewGuid().ToString("N"));
        string codeplugPath = Path.Combine(directory, "codeplug.yml");
        string settingsPath = CreateSettingsPath();
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(codeplugPath, """
                keyFile: "missing-keys.clear"
                systems:
                  - name: "Secure System"
                    identity: "Secure Console"
                    address: "127.0.0.1"
                    port: 62031
                    peerId: 1000001
                    rid: "1001"
                zones:
                  - name: "Dispatch"
                    channels:
                      - name: "Selectable Secure"
                        system: "Secure System"
                        tgid: "101"
                        mode: "p25"
                        keyId: "0x50"
                        algo: "aes"
                        selectable_encryption: true
                """);

            var store = new UserSettingsStore(settingsPath);
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);

            ChannelViewModel channel = Assert.Single(Assert.Single(viewModel.Systems).Channels);
            Assert.True(viewModel.IsCodeplugLoaded);
            Assert.Contains("Encryption keys unavailable:", viewModel.StatusText);
            Assert.Contains("Encrypted P25 channels are disabled.", viewModel.StatusText);
            Assert.False(channel.CanListen);
            Assert.False(channel.CanTransmit);
            Assert.False(channel.CanToggleEncryption);
            Assert.Equal(codeplugPath, store.Load().LastCodeplugPath);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
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
            Assert.Equal(["Alpha Console", "Beta Console"], viewModel.Systems.Select(system => system.Identity));
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
            Assert.Equal(["Dispatch Patch", "Operations Select"], viewModel.PatchGroups.Select(group => group.Name));
            PatchGroupEditorViewModel patchGroup = Assert.Single(viewModel.PatchGroups, group => group.IsPatchGroup);
            PatchGroupEditorViewModel multiSelectGroup = Assert.Single(viewModel.PatchGroups, group => group.IsMultiSelect);
            Assert.Equal(5, patchGroup.Members.Count);
            Assert.True(multiSelectGroup.IsEnabled);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task SubscriberCommandValidationAuditsAndBoundsFailures()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(path, new UserSettingsStore(settingsPath));
            SystemViewModel system = viewModel.Systems[0];

            Assert.False(viewModel.TrySendSubscriberCommand(
                system,
                P25SubscriberCommand.CallAlert,
                "0",
                out string invalidMessage));
            Assert.Contains("1 to 16777215", invalidMessage);
            Assert.False(viewModel.TrySendSubscriberCommand(
                system,
                P25SubscriberCommand.RadioCheck,
                "2002",
                out string disconnectedMessage));
            Assert.Contains("not connected", disconnectedMessage);

            for (uint destinationId = 1; destinationId <= 60; destinationId++)
            {
                Assert.False(viewModel.TrySendSubscriberCommand(
                    system,
                    P25SubscriberCommand.Inhibit,
                    destinationId.ToString(CultureInfo.InvariantCulture),
                    out _));
            }

            Assert.Equal(50, viewModel.SubscriberCommandAudit.Count);
            Assert.Equal((uint)60, viewModel.SubscriberCommandAudit[0].DestinationId);
            Assert.Equal((uint)11, viewModel.SubscriberCommandAudit[^1].DestinationId);
            Assert.All(viewModel.SubscriberCommandAudit, entry => Assert.False(entry.Succeeded));
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
            viewModel.ProcessTraffic(system, new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                42,
                101,
                0,
                "GROUP",
                "TERMINATOR",
                "TERMINATOR_WITH_LC",
                4,
                78,
                new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes]));

            Assert.Equal(2, viewModel.CallHistory.Count);
            Assert.Equal((uint)78, viewModel.CallHistory[0].StreamId);
            Assert.False(viewModel.CallHistory[0].IsActive);
            Assert.NotNull(viewModel.CallHistory[0].Duration);
            Assert.Equal((uint)77, viewModel.CallHistory[1].StreamId);
            Assert.Equal("Alpha Dispatch", viewModel.CallHistory[1].ChannelName);

            viewModel.CallHistoryFilterText = "Alpha Dispatch";
            Assert.Equal(2, viewModel.FilteredCallHistory.Count);
            viewModel.CallHistoryFilterText = "78";
            Assert.Single(viewModel.FilteredCallHistory);
            viewModel.CallHistoryFilterText = "not present";
            Assert.Empty(viewModel.FilteredCallHistory);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task DerivesCallHistoryEncryptionFromP25AndDmrProtocolMetadata()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dvmconsole-codeplug-tests", Guid.NewGuid().ToString("N"));
        string codeplugPath = Path.Combine(directory, "codeplug.yml");
        string settingsPath = CreateSettingsPath();
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(codeplugPath, """
                keyFile: "missing-keys.clear"
                systems:
                  - name: "Alpha"
                    identity: "Alpha Console"
                    address: "127.0.0.1"
                    port: 62031
                    peerId: 1000001
                    rid: "1001"
                zones:
                  - name: "Dispatch"
                    channels:
                      - name: "Secure P25"
                        system: "Alpha"
                        tgid: "102"
                        mode: "p25"
                        keyId: "0x50"
                        algo: "aes"
                      - name: "DMR Dispatch"
                        system: "Alpha"
                        tgid: "101"
                        mode: "dmr"
                        slot: 1
                """);

            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(settingsPath));
            SystemViewModel system = Assert.Single(viewModel.Systems);

            viewModel.ProcessTraffic(system, new FneTrafficFrame(
                FneTrafficProtocol.P25,
                1,
                42,
                102,
                null,
                "GROUP",
                "VOICE",
                "LDU1",
                1,
                80,
                P25DfsiFrameCodec.CreateLdu1Payload(42, 102, new byte[P25DfsiFrameCodec.ImbeBytes])));

            Assert.False(Assert.Single(viewModel.CallHistory).Encrypted);

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
                81,
                new byte[DmrVoicePacketCodec.PacketBytes]));

            byte[] dmrFrame = new byte[DmrVoicePacketCodec.FrameBytes];
            var privacy = new PrivacyLC { AlgId = 3, KId = 0x55, Group = true, DstId = 101 };
            FullLC.EncodePI(privacy, ref dmrFrame);
            byte[] dmrPacket = new byte[DmrVoicePacketCodec.PacketBytes];
            dmrFrame.CopyTo(dmrPacket, DmrVoicePacketCodec.HeaderBytes);
            viewModel.ProcessTraffic(system, new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                42,
                101,
                0,
                "GROUP",
                "DATA_SYNC",
                "VOICE_PI_HEADER",
                2,
                81,
                dmrPacket));

            Assert.Equal(2, viewModel.CallHistory.Count);
            Assert.True(viewModel.CallHistory.Single(entry => entry.Protocol == FneTrafficProtocol.Dmr).Encrypted);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
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
    public async Task SuccessfulLoadsMaintainBoundedRecentCodeplugHistory()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string otherPath = Path.Combine(Path.GetTempPath(), "dvmconsole-other-codeplug.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings { RecentCodeplugPaths = [otherPath, codeplugPath] });

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);

            Assert.Equal([Path.GetFullPath(codeplugPath), Path.GetFullPath(otherPath)], viewModel.RecentCodeplugPaths);
            Assert.Equal(viewModel.RecentCodeplugPaths, store.Load().RecentCodeplugPaths);
            Assert.False(viewModel.HasCodeplugDiagnostics);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task ConnectionDiagnosticsTrackMediaPacketCountsAndMetadata()
    {
        var system = new SystemViewModel(
            new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Test",
            "127.0.0.1:62031");
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.P25,
            1,
            1001,
            2002,
            null,
            "GROUP",
            "VOICE",
            "LDU1",
            7,
            42,
            new byte[] { 1, 2, 3 });

        try
        {
            system.RecordTraffic(traffic);

            Assert.Equal("RX 1 packets / 3 bytes · TX 0 packets / 0 bytes", system.PacketDiagnosticsText);
            Assert.Equal("Test ○", system.SystemTabText);
            Assert.Contains("P25 GROUP/VOICE", system.LastPacketText);
            Assert.Contains("seq 7", system.LastPacketText);
            Assert.Contains("stream 42", system.LastPacketText);
            Assert.Contains("1001→2002", system.LastPacketText);

            system.ApplyStatus(new FneConnectionStatus(
                "Test",
                FneConnectionState.Connected,
                "FNE peer connected",
                DateTimeOffset.UtcNow));
            Assert.Equal("Test ●", system.SystemTabText);
        }
        finally
        {
            await system.DisposeAsync();
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
            PatchGroupEditorViewModel group = Assert.Single(viewModel.PatchGroups, candidate => candidate.IsPatchGroup);
            Assert.True(group.IsEnabled);
            Assert.Equal(
                ["Alpha Dispatch", "Beta Dispatch"],
                group.Members.Where(member => member.IsMember).Select(member => member.Channel.Name));
            PatchGroupEditorViewModel multiSelect = Assert.Single(viewModel.PatchGroups, candidate => candidate.IsMultiSelect);
            Assert.True(multiSelect.IsEnabled);
            Assert.Equal(
                ["Alpha Emergency", "Beta Operations"],
                multiSelect.Members.Where(member => member.IsMember).Select(member => member.Channel.Name));
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task SurfacesOverlappingPatchAndMultiSelectMemberships()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        store.Save(new UserSettings
        {
            PatchGroupMemberships = new Dictionary<string, List<PatchMemberSetting>>
            {
                ["Dispatch Patch"] =
                [
                    new PatchMemberSetting { SystemName = "Alpha", DestinationId = 101 },
                    new PatchMemberSetting { SystemName = "Beta", DestinationId = 201 }
                ],
                ["Operations Select"] =
                [
                    new PatchMemberSetting { SystemName = "Alpha", DestinationId = 101 },
                    new PatchMemberSetting { SystemName = "Beta", DestinationId = 202 }
                ]
            }
        });

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);

            Assert.All(viewModel.PatchGroups, group => Assert.True(group.HasConflicts));
            PatchGroupEditorViewModel patch = Assert.Single(viewModel.PatchGroups, group => group.IsPatchGroup);
            PatchMemberEditorViewModel overlappingMember = Assert.Single(
                patch.Members,
                member => member.IsMember && member.HasConflict);
            Assert.Contains("Operations Select", overlappingMember.ConflictText);
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
