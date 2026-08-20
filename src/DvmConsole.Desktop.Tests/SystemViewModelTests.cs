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

public sealed class SystemViewModelTests
{
    [Fact]
    public void SmokeResultOptionDoesNotBecomeTheConfigurationPath()
    {
        string[] arguments =
        [
            "--smoke-windows",
            "--smoke-result=/tmp/dvmconsole smoke.txt",
            "/tmp/codeplug.yml"
        ];

        Assert.Equal(
            "/tmp/dvmconsole smoke.txt",
            Program.ReadOption(arguments, "--smoke-result="));
        Assert.Equal(
            "/tmp/codeplug.yml",
            arguments.First(argument => !argument.StartsWith("-", StringComparison.Ordinal)));
    }

    [Fact]
    public void RevealRecordingUsesFinderSelectionOnMacOS()
    {
        string path = Path.Combine(Path.GetTempPath(), "recording with spaces.wav");

        System.Diagnostics.ProcessStartInfo startInfo =
            MainWindowViewModel.CreateRevealRecordingStartInfo(path, isWindows: false, isMacOS: true);

        Assert.Equal("/usr/bin/open", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(["-R", Path.GetFullPath(path)], startInfo.ArgumentList);
    }

    [Fact]
    public void RevealRecordingUsesExplorerSelectionOnWindows()
    {
        string path = Path.Combine(Path.GetTempPath(), "recording with spaces.wav");

        System.Diagnostics.ProcessStartInfo startInfo =
            MainWindowViewModel.CreateRevealRecordingStartInfo(path, isWindows: true, isMacOS: false);

        Assert.Equal("explorer.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(["/select,", Path.GetFullPath(path)], startInfo.ArgumentList);
    }

    [Fact]
    public void RevealRecordingOpensContainingFolderOnOtherPlatforms()
    {
        string folder = Path.Combine(Path.GetTempPath(), "recordings");
        string path = Path.Combine(folder, "call.wav");

        System.Diagnostics.ProcessStartInfo startInfo =
            MainWindowViewModel.CreateRevealRecordingStartInfo(path, isWindows: false, isMacOS: false);

        Assert.Equal(Path.GetFullPath(folder), startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Theory]
    [InlineData(400, 44, 2000, 600, 444)]
    [InlineData(1380, 44, 2000, 600, 1400)]
    [InlineData(20, -44, 2000, 600, 0)]
    [InlineData(100, 44, 500, 600, 0)]
    public void ScrollViewportAnchorOffsetTracksInsertedRowsAndClampsToBounds(
        double currentOffset,
        double itemDelta,
        double extentHeight,
        double viewportHeight,
        double expected)
    {
        Assert.Equal(
            expected,
            ScrollViewportAnchorMath.CalculateOffset(
                currentOffset,
                itemDelta,
                extentHeight,
                viewportHeight));
    }

    [Fact]
    public void CallHistoryExposesCompactLocalDateBelowTheTime()
    {
        DateTimeOffset timestamp = new DateTimeOffset(2026, 8, 19, 21, 22, 23, TimeSpan.Zero);
        var entry = new CallHistoryEntry(
            timestamp,
            "Test",
            "Dispatch",
            1001,
            100,
            FneTrafficProtocol.P25,
            42);

        Assert.Equal(timestamp.ToLocalTime().ToString("HH:mm:ss"), entry.TimestampText);
        Assert.Equal(timestamp.ToLocalTime().ToString("yyyy-MM-dd"), entry.DateText);
    }

    [Fact]
    public void ActivityHistoryIncludesRecordingOnlyCatalogEntries()
    {
        var recordingOnly = new CallHistoryEntry(
            DateTimeOffset.UtcNow,
            "SKYNET",
            "CHP Maroon/Bronze",
            P25Defines.WUID_FNE,
            2947,
            FneTrafficProtocol.P25,
            77,
            isRecordingOnly: true);
        var otherSystem = new CallHistoryEntry(
            DateTimeOffset.UtcNow,
            "OTHER",
            "Dispatch",
            42,
            100,
            FneTrafficProtocol.Dmr,
            78);

        CallHistoryEntry[] selected = MainWindowViewModel.SelectActivityHistory(
            [recordingOnly, otherSystem],
            "SKYNET",
            selectedZoneChannelNames: null);

        Assert.Same(recordingOnly, Assert.Single(selected));
        Assert.True(selected[0].IsRecordingOnly);
    }

    [Fact]
    public void RecordingCatalogSnapshotRejectsConcurrentCompletionDeletionAndNewerScans()
    {
        Assert.True(MainWindowViewModel.IsRecordingCatalogSnapshotCurrent(4, 4, 10, 10, false));
        Assert.False(MainWindowViewModel.IsRecordingCatalogSnapshotCurrent(4, 4, 10, 11, false));
        Assert.False(MainWindowViewModel.IsRecordingCatalogSnapshotCurrent(4, 5, 10, 10, false));
        Assert.False(MainWindowViewModel.IsRecordingCatalogSnapshotCurrent(4, 4, 10, 10, true));
    }

    [Fact]
    public void OperatorToolSectionsFollowConsoleSettingsTabOrder()
    {
        Assert.Equal(
            [
                OperatorToolSection.General,
                OperatorToolSection.Audio,
                OperatorToolSection.Tones,
                OperatorToolSection.Streams,
                OperatorToolSection.Recorder,
                OperatorToolSection.History,
                OperatorToolSection.Groups,
                OperatorToolSection.Connections,
                OperatorToolSection.Ptt
            ],
            Enum.GetValues<OperatorToolSection>().Where(section => section != OperatorToolSection.Clock));
    }

    [Theory]
    [InlineData("duplex", true, "duplex", false, true)]
    [InlineData("input-default", true, "output-default", true, true)]
    [InlineData("input", false, "output", false, false)]
    [InlineData("input-default", true, "output", false, false)]
    public void IdentifiesAppleVoiceProcessingCompatibleDevicePairs(
        string inputId,
        bool inputIsDefault,
        string outputId,
        bool outputIsDefault,
        bool expected)
    {
        var input = new AudioDeviceOptionViewModel(inputId, "Input", inputIsDefault);
        var output = new AudioDeviceOptionViewModel(outputId, "Output", outputIsDefault);

        Assert.Equal(expected, MainWindowViewModel.IsAppleVoiceProcessingDevicePairCompatible(input, output));
    }

    [Fact]
    public void PlansFneKeyRequestsEvenWhenLocalFallbackKeysAreAvailable()
    {
        const string aesKey = "00112233445566778899AABBCCDDEEFF";
        using var keyRing = new P25KeyRing("Alpha", new DvmConsole.Core.Configuration.KeyContainer
        {
            Keys =
            [
                new DvmConsole.Core.Configuration.KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = fnecore.P25.P25Defines.P25_ALGO_AES,
                    Key = aesKey
                }
            ]
        });
        var secure = new ChannelViewModel(new DvmConsole.Core.Configuration.ChannelConfiguration
        {
            Name = "Secure",
            System = "Alpha",
            Tgid = "101",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50"
        }, keyRing);
        var duplicate = new ChannelViewModel(new DvmConsole.Core.Configuration.ChannelConfiguration
        {
            Name = "Secure duplicate",
            System = "Alpha",
            Tgid = "102",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50"
        }, keyRing);

        IReadOnlyList<(byte AlgorithmId, ushort KeyId)> requests =
            MainWindowViewModel.ResolveConfiguredP25KeyRequests([secure, duplicate]);

        Assert.Equal([(fnecore.P25.P25Defines.P25_ALGO_AES, (ushort)0x50)], requests);
        Assert.True(secure.CanListen);
    }

    [Fact]
    public void ReportsUnreleasedSemanticVersion()
        => Assert.StartsWith("0.3.1", MainWindow.ApplicationVersion, StringComparison.Ordinal);

    [Theory]
    [InlineData("0.1.0-alpha.1+abcdef123456", "0.1.0-alpha.1 (abcdef1)")]
    [InlineData("0.1.0-alpha.1", "0.1.0-alpha.1")]
    [InlineData("0.1.0+abc", "0.1.0 (abc)")]
    public void FormatsCommitVersionLikeGitHub(string version, string expected)
        => Assert.Equal(expected, MainWindow.FormatShortVersion(version));

    [Fact]
    public void DefaultZoneColorsRemainReadableInBothThemes()
    {
        var zone = new ZoneViewModel("Dispatch", [], []);

        Assert.Equal(Color.Parse("#E8EDF3"), Assert.IsType<SolidColorBrush>(zone.TabBrush).Color);
        Assert.Equal(Color.Parse("#18212B"), Assert.IsType<SolidColorBrush>(zone.TabTextBrush).Color);

        zone.SetDarkMode(true);

        Assert.Equal(Color.Parse("#151D26"), Assert.IsType<SolidColorBrush>(zone.TabBrush).Color);
        Assert.Equal(Color.Parse("#DCE3EB"), Assert.IsType<SolidColorBrush>(zone.TabTextBrush).Color);
    }

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
            Assert.Contains("Encrypted P25 channels are disabled until FNE/KMM supplies their keys.", viewModel.StatusText);
            Assert.True(viewModel.HasCodeplugDiagnostics);
            viewModel.DismissCodeplugDiagnostics();
            Assert.False(viewModel.HasCodeplugDiagnostics);
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

            Assert.Equal(2, viewModel.ActivityCallHistory.Count);
            Assert.Single(viewModel.ActivitySubscriberCommandAudit);
            Assert.Equal("All channels", viewModel.ActivityFilterButtonText);

            alpha.SelectedZone = alpha.Zones[1];
            viewModel.ToggleActivityCurrentZoneFilter();

            Assert.Equal("Current tab", viewModel.ActivityFilterButtonText);
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
            var historyChanges = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)viewModel.FilteredCallHistory).CollectionChanged +=
                (_, args) => historyChanges.Add(args.Action);
            DateTimeOffset start = DateTimeOffset.UnixEpoch;
            byte[] dmrWithQuality = new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes];
            dmrWithQuality[53] = 3;
            dmrWithQuality[54] = 72;

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
                dmrWithQuality),
                receivedAt: start);
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
                new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes]),
                receivedAt: start.AddMilliseconds(20));
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
                new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes]),
                receivedAt: start.AddSeconds(1));
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
                new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes]),
                receivedAt: start.AddSeconds(2));
            viewModel.ProcessTraffic(system, new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                42,
                999,
                1,
                "GROUP",
                "DATA_SYNC",
                "TERMINATOR_WITH_LC",
                5,
                999,
                new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes]),
                receivedAt: start.AddSeconds(3));

            CallHistoryEntry[] sessionHistory = viewModel.CallHistory.Where(entry => !entry.IsRecordingOnly).ToArray();
            Assert.Equal(2, sessionHistory.Length);
            Assert.Contains("non-call DMR terminators 1", system.PacketDiagnosticsText);
            Assert.Equal((uint)78, sessionHistory[0].StreamId);
            Assert.False(sessionHistory[0].IsActive);
            Assert.NotNull(sessionHistory[0].Duration);
            Assert.Equal((uint)77, sessionHistory[1].StreamId);
            Assert.Equal("Alpha Dispatch", sessionHistory[1].ChannelName);
            Assert.True(sessionHistory[1].IsActive);
            Assert.Equal("Info", viewModel.DebugLogSeverityFilter);
            Assert.All(viewModel.FilteredDebugLogs, entry => Assert.Equal(DvmConsole.Core.Diagnostics.DebugLogSeverity.Info, entry.Severity));
            Assert.Contains(viewModel.FilteredDebugLogs, entry => entry.Message.Contains("RX call started", StringComparison.Ordinal));
            Assert.Contains(viewModel.FilteredDebugLogs, entry => entry.Message.Contains("RX call ended", StringComparison.Ordinal));
            Assert.Contains(viewModel.FilteredDebugLogs, entry => entry.Message.Contains("FNE BER errors 3/141", StringComparison.Ordinal));
            Assert.Contains(viewModel.FilteredDebugLogs, entry => entry.Message.Contains("RSSI -72 dBm", StringComparison.Ordinal));

            viewModel.DebugLogSeverityFilter = "Debug";
            Assert.DoesNotContain(viewModel.DebugLogEntries, entry => entry.Message.Contains("FNE RX DMR", StringComparison.Ordinal));

            viewModel.CallHistoryFilterText = "Alpha Dispatch";
            Assert.Equal(2, viewModel.FilteredCallHistory.Count(entry => !entry.IsRecordingOnly));
            viewModel.CallHistoryFilterText = "78";
            Assert.Single(viewModel.FilteredCallHistory, entry => !entry.IsRecordingOnly);
            viewModel.CallHistoryFilterText = "not present";
            Assert.Empty(viewModel.FilteredCallHistory);
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, historyChanges);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task TimeoutGraceResumesOneHistoryCallAndExplicitEndRejectsLateVoice()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                path,
                new UserSettingsStore(settingsPath));
            SystemViewModel system = viewModel.Systems[0];
            ChannelViewModel channel = system.Channels.Single(candidate => candidate.Name == "Alpha Dispatch");

            viewModel.ProcessTraffic(system, CreateDmrTraffic(77, "VOICE", "VOICE"), receivedAt: now);
            viewModel.ExpireStaleReceiveStates(now.AddSeconds(2.5));

            Assert.Equal(ChannelRuntimeState.Receiving, channel.State);
            Assert.True(Assert.Single(viewModel.CallHistory).IsActive);

            viewModel.ProcessTraffic(
                system,
                CreateDmrTraffic(77, "VOICE", "VOICE", packetSequence: 2),
                receivedAt: now.AddSeconds(3));

            Assert.True(Assert.Single(viewModel.CallHistory).IsActive);

            viewModel.ProcessTraffic(
                system,
                CreateDmrTraffic(77, "TERMINATOR", "TERMINATOR_WITH_LC", packetSequence: 3),
                receivedAt: now.AddSeconds(4));
            viewModel.ProcessTraffic(
                system,
                CreateDmrTraffic(77, "VOICE", "VOICE", packetSequence: 4),
                receivedAt: now.AddSeconds(4.5));

            Assert.Single(viewModel.CallHistory);
            Assert.False(viewModel.CallHistory[0].IsActive);
            Assert.Equal(ChannelRuntimeState.Idle, channel.State);
            Assert.Equal(1, channel.IgnoredLatePacketCount);
            Assert.Contains("late/duplicate 1", viewModel.AudioStatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task CollidingP25StreamsOnOneTalkgroupRemainIndependentUntilTheirTerminators()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                path,
                new UserSettingsStore(settingsPath));
            SystemViewModel system = viewModel.Systems[0];
            ChannelViewModel channel = system.Channels.Single(candidate => candidate.Name == "Alpha Operations");

            viewModel.ProcessTraffic(system, CreateP25Traffic(100, 3_206_227, "VOICE", "LDU1", 1), receivedAt: now);
            viewModel.ProcessTraffic(system, CreateP25Traffic(200, 3_213_659, "VOICE", "LDU1", 1), receivedAt: now.AddMilliseconds(100));
            viewModel.ProcessTraffic(system, CreateP25Traffic(100, 3_206_227, "VOICE", "LDU2", 2), receivedAt: now.AddMilliseconds(200));

            Assert.Equal(2, viewModel.CallHistory.Count(entry => entry.IsActive));
            Assert.Equal((uint)100, channel.StreamId);

            viewModel.ProcessTraffic(system, CreateP25Traffic(200, 0, "TERMINATOR", "TDU", 2, destinationId: 0), receivedAt: now.AddSeconds(1));

            Assert.True(viewModel.CallHistory.Single(entry => entry.StreamId == 100).IsActive);
            Assert.False(viewModel.CallHistory.Single(entry => entry.StreamId == 200).IsActive);
            Assert.Equal(ChannelRuntimeState.Receiving, channel.State);

            viewModel.ProcessTraffic(system, CreateP25Traffic(100, 0, "TERMINATOR", "TDU", 3, destinationId: 0), receivedAt: now.AddSeconds(1.1));

            Assert.All(viewModel.CallHistory, entry => Assert.False(entry.IsActive));
            Assert.Equal(ChannelRuntimeState.Idle, channel.State);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task DuplicateZoneCopiesShareOneInboundVoiceStream()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dvmconsole-duplicate-resource-tests", Guid.NewGuid().ToString("N"));
        string codeplugPath = Path.Combine(directory, "codeplug.yml");
        string settingsPath = CreateSettingsPath();
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(codeplugPath, """
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
                      - name: "Alpha Dispatch"
                        system: "Alpha"
                        tgid: "101"
                        mode: "dmr"
                        slot: 1
                  - name: "Operations"
                    channels:
                      - name: "Alpha Dispatch Copy"
                        system: "Alpha"
                        tgid: "101"
                        mode: "dmr"
                        slot: 1
                """);

            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(settingsPath));
            SystemViewModel system = Assert.Single(viewModel.Systems);
            foreach (ChannelViewModel channel in system.Channels)
                channel.SetAudioEnabled(true);

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
                new byte[DmrVoicePacketCodec.PacketBytes]));

            Assert.Single(viewModel.CallHistory);
            Assert.Single(system.Channels, channel => channel.State == ChannelRuntimeState.Receiving);
            Assert.All(system.Zones, zone => Assert.True(zone.IsReceiving));
            Assert.All(system.Channels, channel =>
            {
                Assert.True(channel.IsReceivePresentationActive);
                Assert.Equal(
                    Color.Parse("#008A3A"),
                    Assert.IsType<SolidColorBrush>(channel.CardBackgroundBrush).Color);
            });

            system.Channels[1].SetAudioEnabled(false);

            Assert.False(system.Channels[1].IsReceivePresentationActive);
            Assert.NotEqual(
                Color.Parse("#008A3A"),
                Assert.IsType<SolidColorBrush>(system.Channels[1].CardBackgroundBrush).Color);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task P25HistoryUsesEmbeddedSubscriberAndKeepsPlaceholderCallsVisible()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                path,
                new UserSettingsStore(settingsPath));
            SystemViewModel system = viewModel.Systems.Single(candidate => candidate.Name == "Alpha");
            byte[] identifiedPayload = P25DfsiFrameCodec.CreateLdu1Payload(
                sourceId: 4_500_355,
                destinationId: 102,
                imbe: new byte[P25DfsiFrameCodec.ImbeBytes]);
            DateTimeOffset start = DateTimeOffset.UnixEpoch;

            viewModel.ProcessTraffic(system, new FneTrafficFrame(
                FneTrafficProtocol.P25,
                peerId: 1,
                sourceId: P25Defines.WUID_FNE,
                destinationId: 102,
                slot: null,
                callType: "GROUP",
                frameType: "VOICE",
                subtype: "LDU1",
                packetSequence: 1,
                streamId: 77,
                payload: identifiedPayload),
                receivedAt: start);

            CallHistoryEntry identified = Assert.Single(viewModel.CallHistory);
            Assert.Equal((uint)4_500_355, identified.SourceId);
            Assert.Equal((uint)102, identified.DestinationId);

            viewModel.ProcessTraffic(system, new FneTrafficFrame(
                FneTrafficProtocol.P25,
                peerId: 1,
                sourceId: P25Defines.WUID_FNE,
                destinationId: 102,
                slot: null,
                callType: "GROUP",
                frameType: "VOICE",
                subtype: "LDU1",
                packetSequence: 2,
                streamId: 78,
                payload: new byte[P25DfsiFrameCodec.ClearLduPayloadLength]),
                receivedAt: start.AddSeconds(1));

            Assert.Equal(2, viewModel.CallHistory.Count);
            CallHistoryEntry placeholder = Assert.Single(
                viewModel.CallHistory,
                entry => entry.StreamId == 78);
            Assert.Equal(P25Defines.WUID_FNE, placeholder.SourceId);
            Assert.Contains(placeholder, viewModel.ActivityCallHistory);
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
            var privacy = new PrivacyLC
            {
                AlgId = DmrPrivacyAlgorithms.Arc4,
                KId = 0x55,
                FID = DmrPrivacyAlgorithms.FeatureId,
                Group = true,
                DstId = 101
            };
            FullLC.EncodePI(privacy, ref dmrFrame);
            new SlotType { ColorCode = 0, DataType = (byte)DMRDataType.VOICE_PI_HEADER }.GetData(ref dmrFrame);
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
            Assert.Equal(
                "Secure · RC4",
                viewModel.CallHistory.Single(entry => entry.Protocol == FneTrafficProtocol.Dmr).EncryptionText);
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
    public async Task SystemReceiveActivityTracksChannelsIndependentlyOfSelection()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "Test",
            Tgid = "2002",
            Mode = "p25"
        });
        var system = new SystemViewModel(
            new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Test",
            "127.0.0.1:62031",
            [channel],
            [],
            accentIndex: 1);
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
            []);

        try
        {
            Assert.Equal("○", system.StatusGlyph);
            Assert.False(system.IsReceiving);
            Assert.True(channel.TryApplyTraffic("Test", traffic));
            Assert.True(system.IsReceiving);
            Assert.Equal(1.0, system.ActivityBarOpacity);
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(SystemAccentPalette.GetBrush(1)).Color,
                Assert.IsType<SolidColorBrush>(system.StatusAccentBrush).Color);
        }
        finally
        {
            await system.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectionDiagnosticsCoalesceRepeatedPacketNotifications()
    {
        var system = new SystemViewModel(
            new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Test",
            "127.0.0.1:62031");
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.P25, 1, 1001, 2002, null, "GROUP", "VOICE", "LDU1", 7, 42,
            new byte[] { 1, 2, 3 });
        int notifications = 0;
        system.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SystemViewModel.PacketDiagnosticsText) or nameof(SystemViewModel.LastPacketText))
                notifications++;
        };

        try
        {
            for (int index = 0; index < 20; index++)
                system.RecordTraffic(traffic, publishDiagnostics: false);
            system.PublishTrafficDiagnostics();

            Assert.Equal(2, notifications);
            Assert.StartsWith("RX 20 packets / 60 bytes", system.PacketDiagnosticsText, StringComparison.Ordinal);
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
            Assert.True(viewModel.ConnectionChimes);
            viewModel.UiFontSize = 16;
            viewModel.UiScale = 1.25;
            Assert.Equal(16, store.Load().UiFontSize);
            Assert.Equal(1.25, store.Load().UiScale);
            Assert.Equal(1.25, viewModel.UiScaleTransform.ScaleX);
            Assert.Equal(1.25, viewModel.UiScaleTransform.ScaleY);
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
    public async Task PersistsPlatformAppropriateAudioProcessingMode()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        var store = new UserSettingsStore(settingsPath);

        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(codeplugPath, store);
            Assert.Equal(
                OperatingSystem.IsMacOSVersionAtLeast(26),
                viewModel.IsHighQualityBluetoothAudioAvailable);

            viewModel.AudioInputDeviceIdText = "input-device-42";
            viewModel.AudioOutputDeviceIdText = "output-device-84";
            viewModel.HighQualityBluetoothAudioEnabled = false;
            viewModel.AudioInputAgcEnabled = true;
            viewModel.AudioInputAgcTargetDbfsText = "-30";
            viewModel.SelectedAudioProcessingMode = "Apple voice processing";
            viewModel.ApplyAudioInputSettingsCommand.Execute(null);

            if (!OperatingSystem.IsMacOS())
            {
                Assert.Single(viewModel.AudioProcessingModeOptions);
                Assert.Equal("DVM Console processing", viewModel.AudioProcessingModeOptions[0]);
                Assert.DoesNotContain("Apple voice processing", viewModel.AudioProcessingModeOptions);
                Assert.Equal("DVM Console processing", viewModel.SelectedAudioProcessingMode);
                Assert.True(viewModel.IsDvmConsoleProcessingSelected);
                UserSettings unsupportedPlatformSettings = store.Load();
                Assert.Equal(UserSettings.DvmConsoleAudioProcessingMode, unsupportedPlatformSettings.AudioProcessingMode);
                Assert.Equal("input-device-42", unsupportedPlatformSettings.AudioInputDeviceId);
                Assert.Equal("output-device-84", unsupportedPlatformSettings.AudioOutputDeviceId);
                Assert.False(unsupportedPlatformSettings.HighQualityBluetoothAudioEnabled);
                Assert.True(unsupportedPlatformSettings.AudioInputAgcEnabled);
                Assert.Equal(-30, unsupportedPlatformSettings.AudioInputAgcTargetDbfs);
                Assert.DoesNotContain("echo cancellation", viewModel.AudioProcessingDescription, StringComparison.OrdinalIgnoreCase);
                return;
            }

            if (!viewModel.IsAppleVoiceProcessingRouteCompatible)
            {
                Assert.True(viewModel.IsDvmConsoleProcessingSelected);
                Assert.DoesNotContain("Apple voice processing", viewModel.AudioProcessingModeOptions);
                Assert.Contains("unavailable", viewModel.AppleVoiceProcessingRouteDescription, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(UserSettings.DvmConsoleAudioProcessingMode, store.Load().AudioProcessingMode);
                return;
            }

            Assert.False(viewModel.IsDvmConsoleProcessingSelected);
            UserSettings appleSettings = store.Load();
            Assert.Equal(UserSettings.AppleVoiceProcessingMode, appleSettings.AudioProcessingMode);
            Assert.Equal("input-device-42", appleSettings.AudioInputDeviceId);
            Assert.Equal("output-device-84", appleSettings.AudioOutputDeviceId);
            Assert.True(appleSettings.AudioInputAgcEnabled);
            Assert.Equal(-30, appleSettings.AudioInputAgcTargetDbfs);
            Assert.False(appleSettings.HighQualityBluetoothAudioEnabled);
            Assert.Contains("echo cancellation", viewModel.AudioProcessingDescription, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("RX vocoder processing is controlled separately", viewModel.AudioProcessingDescription, StringComparison.OrdinalIgnoreCase);

            viewModel.SelectedAudioProcessingMode = "DVM Console processing";
            viewModel.ApplyAudioInputSettingsCommand.Execute(null);

            Assert.True(viewModel.IsDvmConsoleProcessingSelected);
            Assert.Equal(UserSettings.DvmConsoleAudioProcessingMode, store.Load().AudioProcessingMode);
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
            Assert.True(viewModel.IsConfiguredPttKey(KeyboardPttKey.Space));
            Assert.False(viewModel.IsConfiguredPttKey(KeyboardPttKey.F1));
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
            Assert.Contains(KeyboardPttKey.F3, viewModel.GlobalPttKeyOptions);
            Assert.Contains(KeyboardPttKey.F19, viewModel.GlobalPttKeyOptions);
            Assert.Contains(KeyboardPttKey.None, viewModel.GlobalPttKeyOptions);
            viewModel.SelectedGlobalPttKey = KeyboardPttKey.F3;
            await viewModel.ApplyGlobalPttKeySelectionAsync();

            UserSettings saved = store.Load();
            Assert.False(saved.ClockUse24HourTime);
            Assert.False(saved.ClockShowSeconds);
            Assert.True(saved.KeepWindowOnTop);
            Assert.True(saved.TogglePttMode);
            Assert.Equal("F3", saved.GlobalPttKey);
            Assert.True(viewModel.IsConfiguredPttKey(KeyboardPttKey.F3));

            viewModel.SelectedGlobalPttKey = KeyboardPttKey.None;
            await viewModel.ApplyGlobalPttKeySelectionAsync();
            Assert.Equal("None", store.Load().GlobalPttKey);
            Assert.Equal("Keyboard PTT disabled", viewModel.GlobalPttKeyText);
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
                Assert.Contains(channelKey, store.Load().RecordingEnabledChannelKeys, StringComparer.OrdinalIgnoreCase);
            }

            await using MainWindowViewModel restored = MainWindowViewModel.Load(codeplugPath, store);
            ChannelViewModel restoredChannel = restored.Systems[0].Channels[0];

            Assert.True(restoredChannel.IsRecordingEnabled);
            Assert.Equal("Disable TAR", restoredChannel.RecordingConfigurationButtonText);

            restoredChannel.SetRecordingEnabled(false);
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
                Assert.Equal(347, store.Load().ChannelWidgetPositions[channelKey].X);
            }

            await using MainWindowViewModel restored = MainWindowViewModel.Load(codeplugPath, store);
            ChannelViewModel restoredChannel = restored.Systems[0].Channels[0];
            Assert.Equal(347, restoredChannel.WidgetX);
            Assert.Equal(186, restoredChannel.WidgetY);

            restored.ResetLayout();

            Assert.True(restored.LockWidgets);
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
            viewModel.SerialPttPortName = "/dev/cu.zzz";
            viewModel.SerialPttBaudRate = 19_200;

            Assert.True(await viewModel.ApplySerialPttSettingsAsync());
            Assert.Equal([("/dev/cu.zzz", 19_200)], createdConfigurations);
            Assert.Equal(0, Assert.Single(createdSources).StartCount);
            UserSettings saved = store.Load();
            Assert.True(saved.SerialPttEnabled);
            Assert.Equal("/dev/cu.zzz", saved.SerialPttPortName);
            Assert.Equal(19_200, saved.SerialPttBaudRate);

            viewModel.SerialPttEnabled = false;

            Assert.True(await viewModel.ApplySerialPttSettingsAsync());
            Assert.Equal(1, createdSources[0].StopCount);
            Assert.Equal(1, createdSources[0].DisposeCount);
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
