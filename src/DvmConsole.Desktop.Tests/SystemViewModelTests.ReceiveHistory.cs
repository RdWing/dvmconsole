using Avalonia.Media;
using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using fnecore.DMR;
using fnecore.P25;
using System.Collections.Specialized;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed partial class SystemViewModelTests
{
    [Fact]
    public async Task CoalescesRapidReplacementStreamsIntoOneHistoryEpisode()
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
                receivedAt: start.AddSeconds(1.5));
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
                receivedAt: start.AddSeconds(1.6));
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
            viewModel.ProcessTraffic(system, new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                42,
                101,
                0,
                "GROUP",
                "VOICE",
                "VOICE",
                6,
                77,
                new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes]),
                receivedAt: start.AddSeconds(3.1));
            viewModel.ExpireStaleReceiveStates(start.AddSeconds(4));

            CallHistoryEntry[] sessionHistory = viewModel.CallHistory.Where(entry => !entry.IsRecordingOnly).ToArray();
            CallHistoryEntry episode = Assert.Single(sessionHistory);
            Assert.Contains("non-call DMR terminators 1", system.ConnectionHealthText);
            Assert.Equal((uint)77, episode.StreamId);
            Assert.Equal(new uint[] { 77, 78 }, episode.StreamIds);
            Assert.Equal(2, episode.StreamFragmentCount);
            Assert.Equal("DMR · 2 stream fragments", episode.StreamText);
            Assert.Equal("Alpha Dispatch", episode.ChannelName);
            Assert.True(episode.IsActive);
            Assert.Equal("Info", viewModel.DebugLogSeverityFilter);
            Assert.All(viewModel.FilteredDebugLogs, entry => Assert.Equal(DvmConsole.Core.Diagnostics.DebugLogSeverity.Info, entry.Severity));
            Assert.Contains(viewModel.FilteredDebugLogs, entry =>
                entry.Message.Contains("RX logical call episode started", StringComparison.Ordinal) &&
                entry.Message.Contains("primary physical stream 77", StringComparison.Ordinal));
            Assert.Contains(viewModel.FilteredDebugLogs, entry => entry.Message.Contains("RX physical stream ended", StringComparison.Ordinal));
            Assert.Contains(viewModel.FilteredDebugLogs, entry => entry.Message.Contains("FNE BER errors 3/141", StringComparison.Ordinal));
            Assert.Contains(viewModel.FilteredDebugLogs, entry => entry.Message.Contains("RSSI -72 dBm", StringComparison.Ordinal));

            viewModel.DebugLogSeverityFilter = "Debug";
            Assert.DoesNotContain(viewModel.DebugLogEntries, entry => entry.Message.Contains("FNE RX DMR", StringComparison.Ordinal));

            viewModel.CallHistoryFilterText = "Alpha Dispatch";
            Assert.Single(viewModel.FilteredCallHistory, entry => !entry.IsRecordingOnly);
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
    public async Task TimeoutGraceAndTerminatorHoldResumeOneHistoryCall()
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
            viewModel.ExpireStaleReceiveStates(now.AddSeconds(1.5));

            Assert.Equal(ChannelRuntimeState.Receiving, channel.State);
            Assert.True(Assert.Single(viewModel.CallHistory).IsActive);

            viewModel.ProcessTraffic(
                system,
                CreateDmrTraffic(77, "VOICE", "VOICE", packetSequence: 2),
                receivedAt: now.AddSeconds(1.75));

            Assert.True(Assert.Single(viewModel.CallHistory).IsActive);

            viewModel.ProcessTraffic(
                system,
                CreateDmrTraffic(77, "TERMINATOR", "TERMINATOR_WITH_LC", packetSequence: 3),
                receivedAt: now.AddSeconds(2.5));
            viewModel.ProcessTraffic(
                system,
                CreateDmrTraffic(77, "VOICE", "VOICE", packetSequence: 4),
                receivedAt: now.AddSeconds(3));

            Assert.Single(viewModel.CallHistory);
            Assert.True(viewModel.CallHistory[0].IsActive);
            Assert.Equal(ChannelRuntimeState.Receiving, channel.State);
            Assert.Equal(0, channel.IgnoredLatePacketCount);

            viewModel.ExpireStaleReceiveStates(now.AddSeconds(5));
            Assert.False(viewModel.CallHistory[0].IsActive);
            Assert.Equal(ChannelRuntimeState.Idle, channel.State);
            Assert.Contains(viewModel.DebugLogEntries, entry =>
                entry.Message.Contains("RX logical call episode ended", StringComparison.Ordinal) &&
                entry.Message.Contains("1 physical stream", StringComparison.Ordinal));

            viewModel.ProcessTraffic(
                system,
                CreateDmrTraffic(77, "VOICE", "VOICE", packetSequence: 5),
                receivedAt: now.AddSeconds(5.5));
            Assert.Equal(1, channel.IgnoredLatePacketCount);
            Assert.Contains("post-call late 1", viewModel.AudioStatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Fact]
    public async Task NewCallAfterDelayedTimeoutCleanupReplacesTheOldReceivePresentation()
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
            channel.SetAudioEnabled(true);

            viewModel.ProcessTraffic(system, CreateDmrTraffic(77, "VOICE", "VOICE"), receivedAt: now);
            channel.MarkReceivePlaybackActive(42, 77);
            Assert.Contains("stream 77", channel.StateText, StringComparison.Ordinal);

            viewModel.ProcessTraffic(
                system,
                CreateDmrTraffic(78, "VOICE", "VOICE", packetSequence: 2),
                receivedAt: now.AddSeconds(5));

            Assert.Contains("stream 78", channel.StateText, StringComparison.Ordinal);
            Assert.DoesNotContain("stream 77", channel.StateText, StringComparison.Ordinal);
            Assert.False(viewModel.CallHistory.Single(entry => entry.StreamId == 77).IsActive);
            Assert.True(viewModel.CallHistory.Single(entry => entry.StreamId == 78).IsActive);
            Assert.Contains(viewModel.DebugLogEntries, entry =>
                entry.Message.Contains("RX physical stream timed out", StringComparison.Ordinal) &&
                entry.Message.Contains("stream 77", StringComparison.Ordinal));
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
            Assert.True(viewModel.CallHistory.Single(entry => entry.StreamId == 200).IsActive);
            Assert.Equal(ChannelRuntimeState.Receiving, channel.State);

            viewModel.ProcessTraffic(system, CreateP25Traffic(100, 0, "TERMINATOR", "TDU", 3, destinationId: 0), receivedAt: now.AddSeconds(1.1));

            Assert.All(viewModel.CallHistory, entry => Assert.True(entry.IsActive));
            Assert.Equal(ChannelRuntimeState.Idle, channel.State);

            viewModel.ExpireStaleReceiveStates(now.AddSeconds(5.2));
            Assert.All(viewModel.CallHistory, entry => Assert.False(entry.IsActive));
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
            Assert.True(system.Zones[0].IsReceiving);
            Assert.False(system.Zones[1].IsReceiving);
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
            viewModel.ToggleActivityReceiveFilter();
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

}
