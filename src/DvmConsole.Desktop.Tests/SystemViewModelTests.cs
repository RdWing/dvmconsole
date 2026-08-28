using DvmConsole.Audio;
using Avalonia.Media;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Core.Runtime;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using System.Collections.Specialized;
using System.Globalization;
using fnecore.DMR;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed partial class SystemViewModelTests
{
    [Fact]
    public async Task ReceiveScopesDistinguishAllSystemsFromSelectedZone()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(settingsPath));
            viewModel.SelectedSystem = viewModel.Systems[0];
            viewModel.Systems[0].SelectedZone = viewModel.Systems[0].Zones[1];

            Assert.Equal(5, viewModel.GetReceiveScopeChannels(ReceiveSelectionScope.All).Count);
            Assert.Equal(
                viewModel.Systems[0].Zones[1].Channels,
                viewModel.GetReceiveScopeChannels(ReceiveSelectionScope.SelectedZone));
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task SelectedZoneDoesNotGateTrafficForAChannelOnAnotherTab()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(settingsPath));
            SystemViewModel system = viewModel.Systems[0];
            viewModel.SelectedSystem = system;
            system.SelectedZone = system.Zones.Single(zone => zone.Name == "Operations");

            viewModel.ProcessTraffic(system, CreateDmrTraffic(77, "VOICE", "VOICE"));

            ChannelViewModel dispatch = system.Channels.Single(channel => channel.Name == "Alpha Dispatch");
            Assert.Equal(ChannelRuntimeState.Receiving, dispatch.State);
            Assert.Contains(viewModel.CallHistory, entry => entry.ChannelName == "Alpha Dispatch" && entry.IsActive);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task PageAndAlertSelectorsResolveEveryArmedChannelIndependently()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(settingsPath));
            ChannelViewModel[] channels = viewModel.Systems.SelectMany(system => system.Channels).Take(3).ToArray();
            channels[0].SetPageSelected(true);
            channels[1].SetPageSelected(true);
            channels[1].SetAlertSelected(true);
            channels[2].SetAlertSelected(true);

            Assert.Equal(channels[..2], viewModel.ResolvePageToneChannels());
            Assert.Equal(channels[1..], viewModel.ResolveGeneratedToneChannels());
            Assert.Equal(["ALERT 1", "ALERT 2", "ALERT 3"], viewModel.BuiltInAlertTones.Select(tone => tone.Name));
            Assert.Equal(
                [LegacyAlertTone.Alert1, LegacyAlertTone.Alert2, LegacyAlertTone.Alert3],
                viewModel.BuiltInAlertTones.Select(tone => tone.Tone));
            Assert.Equal(
                [
                    "Generate 1 kHz for 3 sec",
                    "Generate alternating 1.5 kHz / 800 Hz tones for 3.36 sec",
                    "Generate eight 1 kHz pulses over 3.6 sec"
                ],
                viewModel.BuiltInAlertTones.Select(tone => tone.Description));
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

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
            Assert.Contains("Clear receive remains available.", viewModel.StatusText);
            Assert.Contains("Secure transmit and encrypted receive require the applicable key", viewModel.StatusText);
            Assert.True(viewModel.HasCodeplugDiagnostics);
            viewModel.DismissCodeplugDiagnostics();
            Assert.False(viewModel.HasCodeplugDiagnostics);
            Assert.True(channel.CanListen);
            Assert.False(channel.CanTransmit);
            Assert.True(channel.CanToggleEncryption);
            channel.EncryptionCommand.Execute(null);
            Assert.True(channel.CanListen);
            Assert.True(channel.CanTransmit);
            Assert.False(channel.CanToggleEncryption);
            await viewModel.FlushUserSettingsAsync();
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
            Assert.Equal("TG 101 - DMR", viewModel.Systems[0].Zones[0].Channels[0].TalkgroupText);
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
    public async Task ActivitySidebarTracksOnlyTheSelectedSystem()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                path,
                new UserSettingsStore(settingsPath));
            SystemViewModel alpha = viewModel.Systems[0];
            SystemViewModel beta = viewModel.Systems[1];

            Assert.Same(alpha, viewModel.SelectedSystem);
            Assert.True(alpha.IsSelected);
            Assert.False(beta.IsSelected);

            viewModel.ProcessTraffic(alpha, new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                42,
                101,
                0,
                "GROUP",
                "VOICE",
                "VOICE",
                1,
                701,
                new byte[DmrVoicePacketCodec.PacketBytes]));
            viewModel.ProcessTraffic(beta, new FneTrafficFrame(
                FneTrafficProtocol.P25,
                2,
                43,
                201,
                null,
                "GROUP",
                "VOICE",
                "LDU1",
                1,
                702,
                P25DfsiFrameCodec.CreateLdu1Payload(43, 201, new byte[P25DfsiFrameCodec.ImbeBytes])));

            Assert.False(viewModel.TrySendSubscriberCommand(
                alpha,
                P25SubscriberCommand.CallAlert,
                "2001",
                out _));
            Assert.False(viewModel.TrySendSubscriberCommand(
                beta,
                P25SubscriberCommand.CallAlert,
                "2002",
                out _));

            viewModel.ToggleActivityReceiveFilter();

            Assert.Single(viewModel.ActivityCallHistory, entry => entry.SystemName == "Alpha");
            Assert.Single(viewModel.ActivitySubscriberCommandAudit, entry => entry.SystemName == "Alpha");

            viewModel.SelectedSystem = beta;

            Assert.False(alpha.IsSelected);
            Assert.True(beta.IsSelected);
            Assert.Single(viewModel.ActivityCallHistory, entry => entry.SystemName == "Beta");
            Assert.Single(viewModel.ActivitySubscriberCommandAudit, entry => entry.SystemName == "Beta");
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task ActivitySidebarCanFilterEventHistoryToTheSelectedZone()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                path,
                new UserSettingsStore(settingsPath));
            SystemViewModel alpha = viewModel.Systems[0];
            viewModel.ProcessTraffic(alpha, new FneTrafficFrame(
                FneTrafficProtocol.Dmr, 1, 42, 101, 0, "GROUP", "VOICE", "VOICE", 1, 701,
                new byte[DmrVoicePacketCodec.PacketBytes]));
            viewModel.ProcessTraffic(alpha, new FneTrafficFrame(
                FneTrafficProtocol.Dmr, 1, 43, 103, 1, "GROUP", "VOICE", "VOICE", 2, 702,
                new byte[DmrVoicePacketCodec.PacketBytes]));
            Assert.False(viewModel.TrySendSubscriberCommand(
                alpha,
                P25SubscriberCommand.CallAlert,
                "2001",
                out _));

            viewModel.ToggleActivityReceiveFilter();

            Assert.Equal(2, viewModel.ActivityCallHistory.Count);
            Assert.Single(viewModel.ActivitySubscriberCommandAudit);
            Assert.Equal("System Wide", viewModel.ActivityZoneFilterButtonText);

            alpha.SelectedZone = alpha.Zones[1];
            viewModel.ToggleActivityZoneFilter();

            Assert.Equal("Zone Wide", viewModel.ActivityZoneFilterButtonText);
            Assert.Single(viewModel.ActivityCallHistory);
            Assert.Equal("Alpha Emergency", viewModel.ActivityCallHistory[0].ChannelName);
            Assert.Single(viewModel.ActivitySubscriberCommandAudit);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task ActivitySidebarShowsRxEnabledChannelsByDefault()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                path,
                new UserSettingsStore(settingsPath));
            SystemViewModel alpha = viewModel.Systems[0];
            ChannelViewModel enabled = alpha.Channels.Single(channel => channel.Name == "Alpha Dispatch");
            enabled.SetAudioEnabled(true);

            viewModel.ProcessTraffic(alpha, new FneTrafficFrame(
                FneTrafficProtocol.Dmr, 1, 42, 101, 0, "GROUP", "VOICE", "VOICE", 1, 701,
                new byte[DmrVoicePacketCodec.PacketBytes]));
            viewModel.ProcessTraffic(alpha, new FneTrafficFrame(
                FneTrafficProtocol.Dmr, 1, 43, 103, 1, "GROUP", "VOICE", "VOICE", 2, 702,
                new byte[DmrVoicePacketCodec.PacketBytes]));

            Assert.Equal("Active", viewModel.ActivityReceiveFilterButtonText);
            Assert.Equal("Alpha Dispatch", Assert.Single(viewModel.ActivityCallHistory).ChannelName);

            viewModel.ToggleActivityReceiveFilter();

            Assert.Equal("All", viewModel.ActivityReceiveFilterButtonText);
            Assert.Equal(2, viewModel.ActivityCallHistory.Count);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task TogglesAllTransmitCapableChannelsInTheSelectedSystem()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(path, new UserSettingsStore(settingsPath));
            viewModel.SelectedSystem = viewModel.Systems[0];

            viewModel.ToggleAllTransmitSelection();

            Assert.All(viewModel.Systems[0].Channels, channel => Assert.True(channel.IsTransmitSelected));
            Assert.All(viewModel.Systems[1].Channels, channel => Assert.False(channel.IsTransmitSelected));
            Assert.Contains("3 transmit-capable", viewModel.TransmitStatusText);

            viewModel.ToggleAllTransmitSelection();

            Assert.All(viewModel.Systems[0].Channels, channel => Assert.False(channel.IsTransmitSelected));
            Assert.Equal("Cleared global TX selection.", viewModel.TransmitStatusText);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task ActiveSystemPttTargetsOnlySelectedResourcesInTheActiveSystem()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                path,
                new UserSettingsStore(settingsPath));
            SystemViewModel alpha = viewModel.Systems[0];
            SystemViewModel beta = viewModel.Systems[1];

            viewModel.SelectedSystem = alpha;
            viewModel.ToggleAllTransmitSelection();
            viewModel.SelectedSystem = beta;
            viewModel.ToggleAllTransmitSelection();

            ChannelViewModel[] alphaTargets = alpha.Channels.Where(channel => channel.CanTransmit).ToArray();
            ChannelViewModel[] betaTargets = beta.Channels.Where(channel => channel.CanTransmit).ToArray();
            Assert.Equal(
                alphaTargets.Length + betaTargets.Length,
                viewModel.GetSelectedTransmitTargets(PttTargetScope.AllSelectedResources).Count);
            Assert.Equal(
                betaTargets,
                viewModel.GetSelectedTransmitTargets(PttTargetScope.ActiveSystem));

            viewModel.SelectedSystem = alpha;

            Assert.Equal(
                alphaTargets,
                viewModel.GetSelectedTransmitTargets(PttTargetScope.ActiveSystem));
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

            await viewModel.FlushUserSettingsAsync();
            Assert.Equal([42u, 1001u], store.Load().RecordingIgnoredSubscriberIds[channel.SettingsKey]);
            Assert.Equal("42, 1001", channel.IgnoredSubscriberIdsText);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task ExposesConsistentActivityAndRecordingControlState()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            var channel = new ChannelViewModel(new DvmConsole.Core.Configuration.ChannelConfiguration
            {
                Name = "Recording test",
                System = "Alpha",
                Tgid = "101",
                Mode = "dmr"
            });

            Assert.True(viewModel.ShowCallHistoryPane);
            Assert.False(viewModel.IsActivitySidebarCollapsed);
            Assert.Equal(250, viewModel.ActivitySidebarWidth);
            Assert.Equal("Enable TAR", channel.RecordingConfigurationButtonText);

            viewModel.ShowCallHistoryPane = false;
            channel.SetRecordingEnabled(true);

            Assert.True(viewModel.IsActivitySidebarCollapsed);
            Assert.Equal(34, viewModel.ActivitySidebarWidth);
            await viewModel.FlushUserSettingsAsync();
            Assert.False(store.Load().ShowCallHistoryPane);
            Assert.Equal("TAR", channel.RecordButtonText);
            Assert.Equal("Disable TAR", channel.RecordingConfigurationButtonText);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task PersistsAndRestoresTarArmedState()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            string channelKey;
            await using (MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store))
            {
                ChannelViewModel channel = viewModel.Systems[0].Channels[0];
                channelKey = channel.SettingsKey;
                channel.SetRecordingEnabled(true);

                Assert.True(channel.IsRecordingEnabled);
                Assert.False(channel.IsAudioEnabled);
                await viewModel.FlushUserSettingsAsync();
                Assert.Contains(channelKey, store.Load().RecordingEnabledChannelKeys, StringComparer.OrdinalIgnoreCase);
            }

            await using MainWindowViewModel restored = MainWindowViewModel.Load(codeplugPath, store);
            ChannelViewModel restoredChannel = restored.Systems[0].Channels[0];

            Assert.True(restoredChannel.IsRecordingEnabled);
            Assert.False(restoredChannel.IsAudioEnabled);
            Assert.Equal("Disable TAR", restoredChannel.RecordingConfigurationButtonText);

            restoredChannel.SetRecordingEnabled(false);
            await restored.FlushUserSettingsAsync();
            Assert.DoesNotContain(
                channelKey,
                store.Load().RecordingEnabledChannelKeys,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task PersistsAndRestoresReceiveEnabledCardsIndependentlyOfTar()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        string[] restoredKeys =
        [
            "Alpha\u001FAlpha Operations",
            "Beta\u001FBeta Operations"
        ];

        try
        {
            store.Save(new UserSettings
            {
                RestoreSelectedChannelsOnStartup = true,
                ReceiveEnabledChannelKeys = restoredKeys.ToList()
            });

            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            ChannelViewModel[] restored = viewModel.Systems
                .SelectMany(system => system.Channels)
                .Where(channel => restoredKeys.Contains(channel.SettingsKey, StringComparer.Ordinal))
                .ToArray();
            ChannelViewModel additional = viewModel.Systems
                .SelectMany(system => system.Channels)
                .First(channel => !restored.Contains(channel));

            Assert.Equal(2, restored.Length);
            Assert.All(restored, channel => Assert.True(channel.IsAudioEnabled));
            Assert.All(restored, channel => Assert.False(channel.IsRecordingEnabled));
            Assert.All(
                viewModel.Systems.SelectMany(system => system.Channels).Where(channel => !restored.Contains(channel)),
                channel => Assert.False(channel.IsAudioEnabled));

            viewModel.SetReceiveSelectionPreference(additional, enabled: true);
            viewModel.SetReceiveSelectionPreference(restored[0], enabled: false);

            await viewModel.FlushUserSettingsAsync();
            UserSettings saved = store.Load();
            Assert.Equal(
                new[] { additional.SettingsKey, restored[1].SettingsKey }
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase),
                saved.ReceiveEnabledChannelKeys);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task MovesPersistsRestoresAndResetsUnlockedChannelWidgets()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            store.Save(new UserSettings { LockWidgets = false });
            string channelKey;
            await using (MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store))
            {
                ChannelViewModel channel = viewModel.Systems[0].Channels[0];
                channelKey = channel.SettingsKey;
                viewModel.MoveChannelWidget(channel, 347, 186, persist: true);

                Assert.Equal(347, channel.WidgetX);
                Assert.Equal(186, channel.WidgetY);
                await viewModel.FlushUserSettingsAsync();
                Assert.Equal(347, store.Load().ChannelWidgetPositions[channelKey].X);
            }

            await using MainWindowViewModel restored = MainWindowViewModel.Load(codeplugPath, store);
            ChannelViewModel restoredChannel = restored.Systems[0].Channels[0];
            Assert.Equal(347, restoredChannel.WidgetX);
            Assert.Equal(186, restoredChannel.WidgetY);

            restored.ResetLayout();

            Assert.True(restored.LockWidgets);
            await restored.FlushUserSettingsAsync();
            Assert.Empty(store.Load().ChannelWidgetPositions);
            Assert.Equal(0, restoredChannel.WidgetX);
            Assert.Equal(0, restoredChannel.WidgetY);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task DefaultWidgetLayoutUsesOneGapAndWrapsBeforeMixedWidthCardsOverflow()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "mixed-card-sizes.yml");
        string settingsPath = CreateSettingsPath();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(settingsPath));
            IReadOnlyList<ChannelViewModel> channels = viewModel.Zones[0].Channels;

            Assert.Equal(13, channels.Count);
            Assert.All(channels, channel => Assert.Equal(channel.CardWidth - 12, channel.CardContentWidth));
            Assert.Equal(160, channels[0].AudioMeterWidth);
            Assert.Equal(223, channels[1].AudioMeterWidth);
            Assert.Equal(318, channels[2].AudioMeterWidth);
            Assert.Equal(0, channels[0].WidgetX);
            Assert.Equal(channels[0].CardWidth + MainWindowViewModel.ChannelWidgetSpacing, channels[1].WidgetX);
            Assert.Equal(channels[1].WidgetX + channels[1].CardWidth + MainWindowViewModel.ChannelWidgetSpacing, channels[2].WidgetX);
            Assert.Equal(0, channels[3].WidgetX);
            Assert.Equal(viewModel.ChannelCardHeight + MainWindowViewModel.ChannelWidgetSpacing, channels[3].WidgetY);
            Assert.Equal(channels[3].CardWidth + MainWindowViewModel.ChannelWidgetSpacing, channels[4].WidgetX);

            foreach (IGrouping<double, ChannelViewModel> row in channels.GroupBy(channel => channel.WidgetY))
            {
                ChannelViewModel[] rowChannels = row.OrderBy(channel => channel.WidgetX).ToArray();
                Assert.All(rowChannels, channel =>
                    Assert.True(channel.WidgetX + channel.CardWidth <= MainWindowViewModel.DefaultWidgetCanvasWidth));
                for (int index = 1; index < rowChannels.Length; index++)
                {
                    Assert.Equal(
                        rowChannels[index - 1].WidgetX + rowChannels[index - 1].CardWidth + MainWindowViewModel.ChannelWidgetSpacing,
                        rowChannels[index].WidgetX);
                }
            }

            double[] rowOffsets = channels.Select(channel => channel.WidgetY).Distinct().Order().ToArray();
            for (int index = 1; index < rowOffsets.Length; index++)
                Assert.Equal(viewModel.ChannelCardHeight + MainWindowViewModel.ChannelWidgetSpacing, rowOffsets[index] - rowOffsets[index - 1]);

            viewModel.ResetLayout();

            Assert.Equal(0, channels[3].WidgetX);
            Assert.Equal(viewModel.ChannelCardHeight + MainWindowViewModel.ChannelWidgetSpacing, channels[3].WidgetY);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task SerialPttSettingsDiscoverPersistReplaceAndDisableTheSelectedDevice()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);
        var createdSources = new List<TestPttSource>();
        var createdConfigurations = new List<(string PortName, int BaudRate)>();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                store,
                serialPortProvider: () => ["/dev/cu.zzz", "/dev/cu.aaa"],
                serialPttFactory: (portName, baudRate) =>
                {
                    createdConfigurations.Add((portName, baudRate));
                    var source = new TestPttSource();
                    createdSources.Add(source);
                    return source;
                });

            Assert.Equal(["/dev/cu.aaa", "/dev/cu.zzz"], viewModel.SerialPttPortOptions);
            Assert.Equal("/dev/cu.aaa", viewModel.SerialPttPortName);

            viewModel.SerialPttEnabled = true;
            viewModel.SerialPttActiveSystemOnly = true;
            viewModel.SerialPttPortName = "/dev/cu.zzz";
            viewModel.SerialPttBaudRate = 19_200;

            Assert.True(await viewModel.ApplySerialPttSettingsAsync());
            Assert.Equal([("/dev/cu.zzz", 19_200)], createdConfigurations);
            Assert.Equal(0, Assert.Single(createdSources).StartCount);
            await viewModel.FlushUserSettingsAsync();
            UserSettings saved = store.Load();
            Assert.True(saved.SerialPttEnabled);
            Assert.True(saved.SerialPttActiveSystemOnly);
            Assert.Equal("/dev/cu.zzz", saved.SerialPttPortName);
            Assert.Equal(19_200, saved.SerialPttBaudRate);

            SystemViewModel activeSystem = viewModel.Systems[0];
            viewModel.SelectedSystem = activeSystem;
            viewModel.ToggleAllTransmitSelection();
            viewModel.SelectedSystem = viewModel.Systems[1];
            viewModel.ToggleAllTransmitSelection();
            Assert.Equal(
                viewModel.Systems[1].Channels.Where(channel => channel.CanTransmit),
                viewModel.GetSerialPttTargets());

            viewModel.SerialPttEnabled = false;

            Assert.True(await viewModel.ApplySerialPttSettingsAsync());
            Assert.Equal(1, createdSources[0].StopCount);
            Assert.Equal(1, createdSources[0].DisposeCount);
            await viewModel.FlushUserSettingsAsync();
            Assert.False(store.Load().SerialPttEnabled);
            Assert.Equal("Serial PTT is disabled.", viewModel.SerialPttStatusText);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task LockedChannelWidgetsIgnoreMoveRequests()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            ChannelViewModel channel = viewModel.Systems[0].Channels[0];

            viewModel.MoveChannelWidget(channel, 500, 500, persist: true);

            Assert.Equal(0, channel.WidgetX);
            Assert.Equal(0, channel.WidgetY);
            Assert.Empty(store.Load().ChannelWidgetPositions);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    private static FneTrafficFrame CreateDmrTraffic(
        uint streamId,
        string frameType,
        string subtype,
        ushort packetSequence = 1)
        => new(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 42,
            destinationId: 101,
            slot: 0,
            callType: "GROUP",
            frameType,
            subtype,
            packetSequence,
            streamId,
            new byte[DmrVoicePacketCodec.PacketBytes]);

    private static FneTrafficFrame CreateP25Traffic(
        uint streamId,
        uint sourceId,
        string frameType,
        string subtype,
        ushort packetSequence,
        uint destinationId = 102)
        => new(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId,
            destinationId,
            slot: null,
            callType: "GROUP",
            frameType,
            subtype,
            packetSequence,
            streamId,
            new byte[P25DfsiFrameCodec.NetworkPayloadBytes]);

    private static string CreateSettingsPath()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-settings-tests",
            $"{Guid.NewGuid():N}",
            "UserSettings.json");
        string recordingRoot = Path.Combine(Path.GetDirectoryName(path)!, "recordings");
        new UserSettingsStore(path).Save(new UserSettings { RecordingRootPath = recordingRoot });
        return path;
    }

    private sealed class TestPttSource : IPttSource
    {
        public event EventHandler<bool>? StateChanged;
        public bool IsPressed { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            if (IsPressed)
            {
                IsPressed = false;
                StateChanged?.Invoke(this, false);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private static void CleanupSettingsPath(string settingsPath)
    {
        string? directory = Path.GetDirectoryName(settingsPath);
        if (File.Exists(settingsPath))
            File.Delete(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
