using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Operations;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private static readonly DateTimeOffset DemoTimelineOrigin =
        new(2026, 8, 24, 16, 0, 0, TimeSpan.Zero);
    private bool networkDisabledDemo;
    private bool demoScenarioInitialized;
    private RuntimeHealthSnapshot? demoRuntimeHealthSnapshot;

    internal bool IsNetworkDisabledDemo => networkDisabledDemo;

    internal void InitializeDemoScenario()
    {
        if (demoScenarioInitialized)
            return;

        demoScenarioInitialized = true;
        networkDisabledDemo = true;
        RecordingCatalogScanShutdown catalogScan = historyRecording.CancelRecordingCatalogScan();
        catalogScan.Cancellation?.Dispose();
        RecordRecordingCatalogMutation();

        StatusText = "NEO deterministic demo · all network connections and outbound traffic are disabled.";
        TransmitStatusText = "Demo TX · local pointer capture · network output disabled.";
        AudioStatusText = "Demo microphone ready · fresh synthetic samples · no hardware opened.";
        TogglePttMode = false;
        demoRuntimeHealthSnapshot = CreateDemoRuntimeHealthSnapshot();

        foreach (SystemViewModel system in Systems)
        {
            system.ApplyStatus(new FneConnectionStatus(
                system.Name,
                FneConnectionState.Disconnected,
                "NEO DEMO — network disabled",
                DemoTimelineOrigin));
        }

        SystemViewModel? northMetro = FindDemoSystem("North Metro");
        SystemViewModel? campus = FindDemoSystem("Campus Network");
        ChannelViewModel? northDispatch = FindDemoChannel("North Dispatch");
        ChannelViewModel? transit = FindDemoChannel("Transit Operations");
        ChannelViewModel? publicWorks = FindDemoChannel("Public Works");
        ChannelViewModel? campusServices = FindDemoChannel("Campus Services");
        ChannelViewModel? events = FindDemoChannel("Events");
        ChannelViewModel? facilities = FindDemoChannel("Facilities");

        if (northMetro is not null)
            SelectedSystem = northMetro;
        if (transit is not null)
            SelectChannel(transit);

        northDispatch?.SetTransmitSelected(true);
        transit?.SetTransmitSelected(true);
        publicWorks?.SetPageSelected(true);
        transit?.SetAlertSelected(true);
        events?.SetPageSelected(true);
        facilities?.SetAlertSelected(true);
        transit?.RestoreRecordingEnabled(true);
        campusServices?.RestoreRecordingEnabled(true);
        publicWorks?.RestoreRecordingEnabled(true);

        if (northDispatch is not null)
        {
            const uint transmitStreamId = 0x4E454F01;
            ObservePttActivationSource(PttActivationSource.LocalChannelControl);
            northDispatch.SetTransmitEnabled(true, transmitStreamId);
            callHistory.AddConsoleTransmission(
                DemoTimelineOrigin.AddMinutes(9),
                northDispatch.Definition.SystemName,
                northDispatch.Name,
                990001,
                northDispatch.Definition.DestinationId,
                ProtocolFor(northDispatch),
                transmitStreamId,
                "NEO Demo Console");
        }

        if (northMetro is not null && transit is not null)
        {
            PresentDemoReceive(northMetro, transit, 42017, 0x4E454F11, 72);
            AddActiveDemoCall(
                transit,
                42017,
                0x4E454F11,
                DemoTimelineOrigin.AddMinutes(8).AddSeconds(54));
        }
        if (campus is not null && campusServices is not null)
        {
            PresentDemoReceive(campus, campusServices, 77104, 0x4E454F21, 48);
            AddActiveDemoCall(
                campusServices,
                77104,
                0x4E454F21,
                DemoTimelineOrigin.AddMinutes(8).AddSeconds(47));
        }

        AddCompletedDemoCall(
            publicWorks,
            33412,
            0x4E454F31,
            DemoTimelineOrigin.AddMinutes(7),
            TimeSpan.FromSeconds(18));
        AddCompletedDemoCall(
            events,
            77802,
            0x4E454F41,
            DemoTimelineOrigin.AddMinutes(6),
            TimeSpan.FromSeconds(42));

        AddDemoRecording(
            publicWorks,
            33412,
            "Field Unit 34",
            0x4E454F31,
            DemoTimelineOrigin.AddMinutes(7),
            TimeSpan.FromSeconds(18));
        AddDemoRecording(
            events,
            77802,
            "Event Team 2",
            0x4E454F41,
            DemoTimelineOrigin.AddMinutes(6),
            TimeSpan.FromSeconds(42));

        callHistory.AddEvent(
            DemoTimelineOrigin.AddMinutes(8).AddSeconds(50),
            "MIC",
            "Microphone sample stale — transmit blocked",
            "local pointer",
            "TX safety");
        callHistory.AddEvent(
            DemoTimelineOrigin.AddMinutes(8).AddSeconds(36),
            "TAR",
            "Finalization retry queued; source recording retained",
            "job 2",
            "depth 3");
        callHistory.AddEvent(
            DemoTimelineOrigin.AddMinutes(8).AddSeconds(21),
            "ROUTE",
            "Receive route recovered after output interruption",
            "generation 4",
            "18 ms");

        AddDebugLog(
            DemoTimelineOrigin.AddMinutes(8).AddSeconds(50),
            "DEMO",
            DebugLogSeverity.Warning,
            "Synthetic microphone freshness fault; no hardware was accessed.");
        AddDebugLog(
            DemoTimelineOrigin.AddMinutes(8).AddSeconds(36),
            "DEMO",
            DebugLogSeverity.Info,
            "Synthetic TAR retry retained its fictional source recording.");

        NotifyCallHistoryChanged();
        (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        NotifyConnectionPresentationChanged();
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectionStatusText)));
    }

    private static RuntimeHealthSnapshot CreateDemoRuntimeHealthSnapshot()
        => new(
            DemoTimelineOrigin.AddMinutes(9),
            new ReceiveQueueHealth(
                CurrentDepth: 4,
                PeakDepth: 19,
                CoalescedWakeCount: 61,
                SpuriousWakeCount: 0),
            new MicrophoneHealth(
                MicrophoneHealthState.Ready,
                CaptureGeneration: 4,
                LastSampleAge: TimeSpan.FromMilliseconds(12),
                CallbackCadence: TimeSpan.FromMilliseconds(20),
                Fault: null),
            new WorkBacklogHealth(
                Depth: 1,
                PeakDepth: 2,
                OldestAge: TimeSpan.FromMilliseconds(20),
                Stage: "network-disabled demonstration",
                LastError: null),
            new WorkBacklogHealth(
                Depth: 3,
                PeakDepth: 7,
                OldestAge: TimeSpan.FromSeconds(45),
                Stage: "retrying",
                LastError: "fictional transient finalizer fault"),
            new CatalogScanHealth(
                FilesSeen: 48,
                Loaded: 47,
                Expired: 2,
                Damaged: 1,
                Inaccessible: 0,
                Duration: TimeSpan.FromMilliseconds(34)),
            RouteRecoveryAttempts: 1,
            LastRouteRecoveryDuration: TimeSpan.FromMilliseconds(18),
            LastRouteRecoveryResult: "recovered generation 4",
            new LatencyPercentiles(
                P50: TimeSpan.FromMilliseconds(24),
                P95: TimeSpan.FromMilliseconds(52),
                P99: TimeSpan.FromMilliseconds(88)));

    private SystemViewModel? FindDemoSystem(string name)
        => Systems.FirstOrDefault(system => system.Name.Equals(name, StringComparison.Ordinal));

    private ChannelViewModel? FindDemoChannel(string name)
        => Systems
            .SelectMany(system => system.Channels)
            .FirstOrDefault(channel => channel.Name.Equals(name, StringComparison.Ordinal));

    private static void PresentDemoReceive(
        SystemViewModel system,
        ChannelViewModel channel,
        uint sourceId,
        uint streamId,
        double audioLevel)
    {
        var traffic = new FneTrafficFrame(
            FneTrafficProtocolMapper.FromChannelProtocol(channel.SessionDefinition.Protocol),
            peerId: 9000000,
            sourceId,
            channel.Definition.DestinationId,
            channel.Definition.Slot,
            "GROUP",
            "VOICE",
            channel.Definition.Protocol == ChannelProtocol.P25 ? "LDU1" : "VOICE",
            packetSequence: 1,
            streamId,
            ReadOnlySpan<byte>.Empty);
        channel.TryApplyTraffic(system.Name, traffic);
        system.RecordTraffic(traffic);
        channel.SetAudioEnabled(true);
        channel.MarkReceivePlaybackActive(sourceId, streamId);
        channel.SetAudioLevel(audioLevel, ChannelAudioDirection.Receive, streamId);
    }

    private void AddCompletedDemoCall(
        ChannelViewModel? channel,
        uint sourceId,
        uint streamId,
        DateTimeOffset startedAt,
        TimeSpan duration)
    {
        if (channel is null)
            return;

        var entry = new CallHistoryEntry(
            startedAt,
            channel.Definition.SystemName,
            channel.Name,
            sourceId,
            channel.Definition.DestinationId,
            ProtocolFor(channel),
            streamId,
            callerText: sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        entry.Complete(startedAt.Add(duration));
        callHistory.Add(entry);
    }

    private void AddActiveDemoCall(
        ChannelViewModel channel,
        uint sourceId,
        uint streamId,
        DateTimeOffset startedAt)
        => callHistory.Add(new CallHistoryEntry(
            startedAt,
            channel.Definition.SystemName,
            channel.Name,
            sourceId,
            channel.Definition.DestinationId,
            ProtocolFor(channel),
            streamId,
            callerText: sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private void AddDemoRecording(
        ChannelViewModel? channel,
        uint sourceId,
        string alias,
        uint streamId,
        DateTimeOffset startedAt,
        TimeSpan duration)
    {
        if (channel is null)
            return;

        string fileName = $"demo-{channel.Name.ToLowerInvariant().Replace(' ', '-')}.opus";
        var metadata = new CallRecordingMetadata
        {
            RecordingId = $"neo-demo-{streamId:X8}",
            Direction = "RX",
            RecordingSourceType = "InboundRadio",
            Protocol = ProtocolFor(channel).ToString().ToUpperInvariant(),
            UtcStartTime = startedAt,
            UtcEndTime = startedAt.Add(duration),
            DurationMs = (long)duration.TotalMilliseconds,
            FilePath = Path.Combine(
                Path.GetDirectoryName(userSettingsStore.Path) ?? Path.GetTempPath(),
                "Recordings",
                fileName),
            FileName = fileName,
            FileSizeBytes = 0,
            SampleRate = 8_000,
            BitsPerSample = 16,
            ChannelCount = 1,
            OriginalSampleCount = (long)(duration.TotalSeconds * 8_000),
            ActiveSampleCount = (long)(duration.TotalSeconds * 6_400),
            PeakAmplitude = 18_420,
            SystemName = channel.Definition.SystemName,
            ChannelName = channel.Name,
            TalkgroupId = channel.Definition.DestinationId,
            SubscriberId = sourceId,
            SubscriberAlias = alias,
            StreamId = streamId,
            StreamIds = [streamId],
            PlaybackValidated = false
        };
        recordingEntries.Add(metadata);
        callHistory.AddOrAttachRecording(metadata);
    }
}
