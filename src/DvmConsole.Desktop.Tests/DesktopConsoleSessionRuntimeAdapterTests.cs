// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Desktop;
using DvmConsole.Application;
using DvmConsole.Core.Settings;
using DvmConsole.Presentation;
using DvmConsole.FneClient;
using System.Collections.Immutable;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DesktopConsoleSessionRuntimeAdapterTests
{
    [Fact]
    public async Task OperationalSnapshotsSurvivePresentationAdapterReplacement()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-shared-snapshots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")),
                uiDispatcher: new StatusUiDispatcher(), networkDisabledDemo: true);
            await using var first = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            await using var second = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            var before = first.CaptureSnapshot();
            Assert.Same(before, second.CaptureSnapshot());
            await first.DisposeAsync();
            var channel = owner.Systems[0].Channels[0];
            channel.SessionState.Operator.SetPageSelected(!before.Channels[channel.Id].PageSelected);
            var after = second.CaptureSnapshot();
            Assert.NotSame(before, after);
            Assert.Equal(!before.Channels[channel.Id].PageSelected, after.Channels[channel.Id].PageSelected);
            Assert.Single(owner.OperationalRuntime.SnapshotChannels.Where(item => item.State.Id == channel.Id));
            await second.DisposeAsync();
            owner.OutputMuted = true;
            owner.RecordingPlaybackState.Apply(RecordingId.New(), true, channel.Id);
            await using var reattached = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            var current = reattached.CaptureSnapshot().Channels[channel.Id];
            Assert.Equal("global output mute", current.EffectiveMuteReason);
            Assert.True(current.RecordingPlayback);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RetiredDesktopCommandsCannotChangeChannelControls()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-retired-controls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")),
                uiDispatcher: new StatusUiDispatcher(), networkDisabledDemo: true);
            await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            var channel = owner.Systems[0].Channels[0];
            Assert.Same(owner.OperationalRuntime.Commands, adapter.Commands);
            Assert.Equal("DvmConsole.Application", adapter.Commands.GetType().Assembly.GetName().Name);
            channel.SessionState.SetAuthority(TargetAuthorityState.Available);
            await adapter.Commands.SetTransmitSelectedAsync(channel.Id, true);
            Assert.True(channel.IsTransmitSelected);
            Assert.Equal($"{channel.Name} selected for global TX.", owner.TransmitStatusText);
            await adapter.Commands.SetPageSelectedAsync(channel.Id, true);
            Assert.True(channel.IsPageSelected);
            Assert.Equal($"{channel.Name} armed for QCII paging.", owner.TransmitStatusText);
            await adapter.Commands.SetAlertSelectedAsync(channel.Id, true);
            Assert.True(channel.IsAlertSelected);
            Assert.Equal($"{channel.Name} armed for DTMF and alert tones.", owner.TransmitStatusText);
            var before = channel.SessionState.Operator.Snapshot;
            owner.SuppressSessionInputForTransition();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.Commands.SetReceiveEnabledAsync(channel.Id, true).AsTask());
            Assert.False(await adapter.Commands.BeginPttAsync(channel.Id));
            using var canceledRelease = new CancellationTokenSource();
            canceledRelease.Cancel();
            await adapter.Commands.EndPttAsync(channel.Id, canceledRelease.Token);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.Commands.SetTransmitSelectedAsync(channel.Id, !before.TransmitSelected).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.Commands.SetPageSelectedAsync(channel.Id, !before.PageSelected).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.Commands.SetAlertSelectedAsync(channel.Id, !before.AlertSelected).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.Commands.SetTransmitEncryptedAsync(channel.Id, true).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.Commands.SetChannelGainAsync(channel.Id, 0.25).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.Commands.SetChannelBalanceAsync(channel.Id, 0.5).AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => ((IConsoleRecordingCommands)adapter.Commands)
                .SetRecordingEnabledAsync(channel.Id, true).AsTask());
            Assert.Equal(before, channel.SessionState.Operator.Snapshot);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PresentedPcmAdvancesMetersWhileDesktopDispatchIsHeld()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-meter-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = new StatusUiDispatcher();
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")),
                uiDispatcher: dispatcher, networkDisabledDemo: true);
            dispatcher.RunPending();
            dispatcher.HoldInvocations = true;
            try
            {
                await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                    _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
                var channel = owner.Systems[0].Channels[0];
                channel.SetAudioEnabled(true, "meter test");
                channel.MarkReceiveAudioMeterActive(77);
                int cardNotifications = 0;
                channel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ChannelViewModel.AudioMeter)) cardNotifications++;
                };
                var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                adapter.MeterSampled += (_, value) =>
                {
                    if (value.ChannelId == channel.Id && value.Rms > 0) observed.TrySetResult();
                };
                // Inject at the production mixer's callback boundary without opening hardware.
                typeof(MainWindowViewModel).GetMethod("HandlePresentedReceiveSamples",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(owner, [channel.Id, 77u,
                        new ReadOnlyMemory<short>(Enumerable.Repeat((short)12000, 400).ToArray()), TimeSpan.Zero]);
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, cardNotifications);
                Assert.True(channel.AudioLevel > 0);
                dispatcher.RunPending();
                Assert.True(cardNotifications > 0);
            }
            finally { dispatcher.HoldInvocations = false; dispatcher.RunPending(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RuntimeMeterThresholdAndDisposalDoNotRequireCardDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-meter-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")),
                uiDispatcher: new StatusUiDispatcher(), networkDisabledDemo: true);
            await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            var channel = owner.Systems[0].Channels[0];
            int publications = 0;
            ChannelMeterSample? sample = null;
            adapter.MeterSampled += (_, value) => { publications++; sample = value; };
            channel.SessionState.Meter.Update(25, 50, minimumChange: 0.25);
            Assert.Equal(1, publications);
            Assert.Equal(channel.Id, sample!.ChannelId);
            Assert.Equal(25, channel.AudioLevel);
            Assert.Equal(50, channel.AudioPeakLevel);
            channel.SessionState.Meter.Update(25.1, 50.1, minimumChange: 0.25);
            Assert.Equal(1, publications);
            channel.SessionState.Meter.Update(0, 0, minimumChange: 0.25);
            Assert.Equal(2, publications);
            Assert.Equal(0, channel.AudioLevel);
            await adapter.DisposeAsync();
            channel.SessionState.Meter.Update(30, 60);
            Assert.Equal(2, publications);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ApplicationSnapshotsPublishWithoutPumpingTheDesktopUiQueue()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-background-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")),
                uiDispatcher: new StatusUiDispatcher(), networkDisabledDemo: true);
            await using var session = new ConsoleApplicationSession(new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask));
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.SnapshotChanged += (_, args) =>
            {
                if (args.Current.StatusText == "Background publication") published.TrySetResult();
            };
            owner.SessionStatus.SetConsole("Background publication");
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Background publication", session.Snapshot.StatusText);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PlaybackAndMuteSnapshotsDoNotWaitForHistoryOrToolbarDispatch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-runtime-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = new StatusUiDispatcher();
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")),
                uiDispatcher: dispatcher, networkDisabledDemo: true);
            await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            var channel = owner.Systems.SelectMany(system => system.Channels).First();
            var handler = typeof(MainWindowViewModel).GetMethod("HandleRecordingPlaybackStateChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var id = RecordingId.New();
            var identity = new RecordingCallIdentity(DateTimeOffset.UtcNow, channel.Definition.SystemName,
                channel.Name, "RX", channel.Definition.Protocol.ToString(), 42,
                channel.Definition.DestinationId, 10, null);
            handler.Invoke(owner, [null, new RecordingPlaybackStateChangedEventArgs(id, "", true) { Identity = identity }]);
            Assert.True(adapter.CaptureSnapshot().Channels[channel.Id].RecordingPlayback);
            handler.Invoke(owner, [null, new RecordingPlaybackStateChangedEventArgs(id, "", false)]);
            Assert.False(adapter.CaptureSnapshot().Channels[channel.Id].RecordingPlayback);
            owner.OutputMuted = true;
            Assert.Equal("global output mute", adapter.CaptureSnapshot().Channels[channel.Id].EffectiveMuteReason);
            dispatcher.RunPending();
            Assert.False(adapter.CaptureSnapshot().Channels[channel.Id].RecordingPlayback);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FneKeyRetrievalAndDisconnectDoNotWaitForDesktopDispatcher()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-key-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var master = new FneLoopbackMaster();
            using var keys = new DvmConsole.Media.P25KeyRing();
            var dispatcher = new StatusUiDispatcher();
            var options = master.CreateOptions("Key test") with { SourceId = 1001 };
            var channel = new ChannelViewModel(new DvmConsole.Core.Configuration.ChannelConfiguration
            {
                Name = "Secure",
                System = options.Name,
                Mode = "p25",
                Tgid = "100",
                Algo = "aes",
                KeyId = "1"
            }, p25KeyResolver: keys);
            var system = new SystemViewModel(options, options.Name, "Loopback", [channel]);
            var store = new UserSettingsStore(Path.Combine(root, "settings.json"));
            // This fixture exercises network/key state while the UI is held. It
            // must not start native connection-chime playback on the CI host.
            store.Save(new UserSettings { RecordingRootPath = Path.Combine(root, "recordings"), ConnectionChimes = false });
            await using var owner = new MainWindowViewModel("Key test", [system], [],
                new MainWindowViewModelOptions(Security: new(keys), Document: new(store),
                    Host: new(SerialPortProvider: () => [], UiDispatcher: dispatcher),
                    Features: new(NetworkDisabledDemo: true)));
            await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            Assert.False(adapter.CaptureSnapshot().Channels[channel.Id].TransmitKeyAvailable);
            dispatcher.RunPending();
            dispatcher.HoldInvocations = true;
            try
            {
                var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                system.KeyResponseReceived += (_, _) => response.TrySetResult();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await system.StartAsync(timeout.Token);
                Assert.Equal(((byte)0x84, (ushort)1), await master.ReadKeyRequestAsync(timeout.Token));
                byte[] material = Enumerable.Repeat((byte)0x44, 32).ToArray();
                await master.SendKeyResponseAsync(0x84, 1, material, timeout.Token);
                await response.Task.WaitAsync(timeout.Token);
                Assert.True(keys.TryResolve(options.Name, 0x84, 1, out var resolved));
                Assert.Equal(material, resolved.ToArray());
                Assert.True(adapter.CaptureSnapshot().Channels[channel.Id].TransmitKeyAvailable);
                await system.StopAsync(timeout.Token);
                Assert.False(keys.TryResolve(options.Name, 0x84, 1, out _));
                Assert.False(adapter.CaptureSnapshot().Channels[channel.Id].TransmitKeyAvailable);
                dispatcher.RunPending();
                Assert.False(keys.TryResolve(options.Name, 0x84, 1, out _));
            }
            finally { dispatcher.HoldInvocations = false; dispatcher.RunPending(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReceiveIngressAndRetirementDoNotWaitForDesktopProjection()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-receive-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = new StatusUiDispatcher();
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")),
                uiDispatcher: dispatcher, networkDisabledDemo: true);
            dispatcher.RunPending();
            dispatcher.HoldInvocations = true;
            try
            {
                var system = owner.Systems[0];
                var channel = system.Channels[0];
                DateTimeOffset started = DateTimeOffset.UtcNow;
                void Send(ushort sequence, DateTimeOffset timestamp) => owner.HandleSystemTraffic(system,
                    new(SystemId.FromName(system.Name), [channel.Id],
                        new FneTrafficFrame(FneTrafficProtocol.Dmr, 1, 42, 101, 0, "GROUP", "VOICE", "VOICE",
                            sequence, 77, new byte[DvmConsole.Media.DmrVoicePacketCodec.PacketBytes]), timestamp));
                Send(1, started);
                Send(2, started.AddMilliseconds(100));
                var history = owner.PreparedState!.History;
                var record = Assert.Single(history.Snapshot);
                Assert.True(record.IsActive);
                Assert.DoesNotContain(owner.CallHistory, entry => entry.Id == record.Id);
                owner.ExpireStaleReceiveStates(started.AddSeconds(5));
                owner.ExpireStaleReceiveStates(started.AddSeconds(10));
                Assert.False(history.Find(record.Id)!.IsActive);
                dispatcher.RunPending();
                Assert.False(Assert.Single(owner.CallHistory, entry => entry.Id == record.Id).IsActive);
                Assert.Single(history.Snapshot);
            }
            finally { dispatcher.HoldInvocations = false; dispatcher.RunPending(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TransmitStateAndHistoryCompleteWhileDesktopDispatcherIsHeld()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-transmit-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = new StatusUiDispatcher();
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")),
                uiDispatcher: dispatcher, networkDisabledDemo: true);
            dispatcher.RunPending();
            dispatcher.HoldInvocations = true;
            try
            {
                ChannelViewModel channel = owner.Systems[0].Channels[0];
                var start = (IManualTransmitStartContext)owner.ManualTransmitSession;
                var lifecycle = (ITransmitLifecyclePresentation)owner.ManualTransmitSession;
                Assert.True(start.SetStartingAsync([channel.Id]).IsCompletedSuccessfully);
                Assert.True(channel.OperatorState.Snapshot.TransmitStarting);
                channel.SessionState.SetTransmitEnabled(true, 77);
                var history = owner.PreparedState!.History;
                var record = history.BeginTransmit(DateTimeOffset.Now, owner.Systems[0].Name, channel.Name,
                    42, channel.Definition.DestinationId,
                    ChannelProtocolMediaMapper.ToTrafficProtocol(channel.Definition.Protocol),
                    77, channelId: channel.Id);
                Assert.True(lifecycle.StoppingAsync([new(channel.Id, 77)]).IsCompletedSuccessfully);
                Assert.True(channel.OperatorState.Snapshot.TransmitStopping);
                Assert.True(lifecycle.StoppedAsync([channel.Id], [new(channel.Id, 77)],
                    new HashSet<ChannelId>(), TimeSpan.Zero, null, null).IsCompletedSuccessfully);
                Assert.False(channel.OperatorState.Snapshot.TransmitEnabled);
                Assert.False(history.Find(record.Id)!.IsActive);
                Assert.DoesNotContain(owner.CallHistory, entry => entry.Id == record.Id);
                dispatcher.RunPending();
                Assert.False(Assert.Single(owner.CallHistory, entry => entry.Id == record.Id).IsActive);

                channel.SessionState.SetTransmitEnabled(true, 88);
                Assert.True(lifecycle.StoppingAsync([new(channel.Id, 88)]).IsCompletedSuccessfully);
                Assert.True(lifecycle.StoppedAsync([channel.Id], [new(channel.Id, 88)],
                    new HashSet<ChannelId> { channel.Id }, TimeSpan.Zero, new IOException("Stop not confirmed"), null).IsCompletedSuccessfully);
                Assert.True(channel.OperatorState.Snapshot.TransmitEnabled);
                Assert.False(channel.OperatorState.Snapshot.TransmitStopping);
                Assert.True(lifecycle.StartFailedAsync([channel.Id], new IOException("Input unavailable")).IsCompletedSuccessfully);
                Assert.False(channel.OperatorState.Snapshot.TransmitEnabled);
                await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                    _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
                await adapter.Commands.EndPttAsync(channel.Id, new CancellationToken(canceled: true));
            }
            finally { dispatcher.HoldInvocations = false; dispatcher.RunPending(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MixCommandsUpdateSharedStateAndPersistWithoutViewModelCommandEvents()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-channel-mix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new UserSettingsStore(Path.Combine(root, "settings.json"));
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                store, networkDisabledDemo: true);
            await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            ChannelViewModel channel = owner.Systems.SelectMany(system => system.Channels).First();
            int commandEvents = 0;
            channel.VolumeChanged += (_, _) => commandEvents++;
            channel.StereoBalanceChanged += (_, _) => commandEvents++;

            await adapter.Commands.SetChannelGainAsync(channel.Id, 9);
            await adapter.Commands.SetChannelBalanceAsync(channel.Id, -9);
            Assert.Equal(4, channel.Volume);
            Assert.Equal(-1, channel.StereoBalance);
            Assert.Equal(4, adapter.CaptureSnapshot().Channels[channel.Id].Gain);
            Assert.Equal(-1, adapter.CaptureSnapshot().Channels[channel.Id].Balance);
            Assert.Equal(0, commandEvents);
            await owner.FlushUserSettingsAsync();
            UserSettings saved = store.Load();
            Assert.Equal(4, saved.ChannelVolumes[channel.SettingsKey]);
            Assert.Equal(-1, saved.ChannelStereoBalances[channel.SettingsKey]);

            // Existing desktop sliders still use the same persistence/audio path.
            channel.Volume = 0.5;
            channel.StereoBalance = 0.25;
            await owner.FlushUserSettingsAsync();
            saved = store.Load();
            Assert.Equal(0.5, saved.ChannelVolumes[channel.SettingsKey]);
            Assert.Equal(0.25, saved.ChannelStereoBalances[channel.SettingsKey]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SharedStatusDrivesDesktopBindingsAndSnapshotsWithoutRebuildingChannels()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-shared-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = new StatusUiDispatcher();
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")),
                uiDispatcher: dispatcher, networkDisabledDemo: true);
            dispatcher.RunPending();
            await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            var shared = Assert.IsType<ConsoleSessionState>(owner.PreparedState).Status;
            Assert.Same(shared, owner.SessionStatus);
            var original = adapter.CaptureSnapshot();
            ChannelViewModel channelView = owner.Systems.SelectMany(system => system.Channels).First();
            channelView.RefreshReceivePresentation();
            Assert.Same(original, adapter.CaptureSnapshot());
            channelView.RefreshEncryptionState();
            Assert.Same(original, adapter.CaptureSnapshot());
            original = adapter.CaptureSnapshot();
            var notifications = new List<string?>();
            owner.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
            shared.SetConsole("Shared connection status");
            shared.SetAudio("Shared audio status");
            shared.SetTransmit("Shared transmit status");
            Assert.Equal("Shared connection status", owner.StatusText);
            Assert.Equal("Shared audio status", owner.AudioStatusText);
            Assert.Equal("Shared transmit status", owner.TransmitStatusText);
            Assert.Equal("Shared transmit status", owner.CompactStatusText);
            Assert.Empty(notifications);
            dispatcher.RunPending();
            Assert.Contains(nameof(owner.StatusText), notifications);
            Assert.Contains(nameof(owner.AudioStatusText), notifications);
            Assert.Contains(nameof(owner.TransmitStatusText), notifications);
            var updated = adapter.CaptureSnapshot();
            Assert.Equal("Shared connection status", updated.StatusText);
            Assert.NotEqual(updated.StatusText, original.StatusText);
            foreach (var (id, channel) in original.Channels) Assert.Same(channel, updated.Channels[id]);
            shared.SetConsole("Queued before retirement");
            await owner.DisposeAsync();
            notifications.Clear();
            shared.SetConsole("Retired session");
            dispatcher.RunPending();
            Assert.Empty(notifications);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class StatusUiDispatcher : IUiDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> pending = new();
        public bool HoldInvocations { get; set; }
        public bool CheckAccess() => false;
        public void Post(Action action, bool background = false) => pending.Enqueue(action);
        public ValueTask InvokeAsync(Action action)
        {
            if (!HoldInvocations) { action(); return ValueTask.CompletedTask; }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Enqueue(() => { action(); completion.SetResult(); });
            return new(completion.Task);
        }
        public void RunPending() { while (pending.TryDequeue(out var action)) action(); }
    }

    [Fact]
    public async Task PatchSnapshotsChangeOnSavedOperatorStateRatherThanEditorDrafts()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-patch-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var store = new UserSettingsStore(Path.Combine(root, "settings.json"));
        store.Save(new UserSettings
        {
            PatchGroupMemberships = new Dictionary<string, List<PatchMemberSetting>>
            {
                ["Dispatch Patch"] =
                [
                    new() { SystemName = "Alpha", DestinationId = 101 },
                    new() { SystemName = "Beta", DestinationId = 201 }
                ]
            }
        });
        try
        {
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"), store);
            await using var adapter = new DesktopConsoleSessionRuntimeAdapter(owner,
                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask);
            PatchGroupEditorViewModel group = Assert.Single(owner.PatchGroups, group => group.IsPatchGroup);
            PatchMemberEditorViewModel beta = Assert.Single(group.Members,
                member => member.IsMember && member.Channel.SystemName == "Beta");
            var original = adapter.CaptureSnapshot();
            Assert.Single(original.Channels[beta.Channel.Id].Patches);
            beta.IsMember = false;
            Assert.Single(adapter.CaptureSnapshot().Channels[beta.Channel.Id].Patches);
            owner.ApplyPatchGroup(group);
            Assert.Empty(adapter.CaptureSnapshot().Channels[beta.Channel.Id].Patches);
            Assert.Single(original.Channels[beta.Channel.Id].Patches);

            owner.OutputMuted = true;
            Assert.Equal("global output mute", adapter.CaptureSnapshot().Channels[beta.Channel.Id].EffectiveMuteReason);
            var channel = Assert.IsType<ChannelViewModel>(beta.Channel);
            channel.SessionState.Operator.SetAudioEnabled(true);
            channel.SessionState.Operator.SetAudioSuspended(true);
            Assert.Equal("console transmit mute", adapter.CaptureSnapshot().Channels[beta.Channel.Id].EffectiveMuteReason);
            channel.SessionState.Operator.SetAudioSuspended(false);
            owner.OutputMuted = false;
            Assert.Null(adapter.CaptureSnapshot().Channels[beta.Channel.Id].EffectiveMuteReason);
            Assert.Null(original.Channels[beta.Channel.Id].EffectiveMuteReason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void IncrementalProjectionRebuildsOnlyDirtyEntries()
    {
        ImmutableDictionary<int, object> previous = new Dictionary<int, object>
        {
            [1] = new object(),
            [2] = new object(),
            [3] = new object()
        }.ToImmutableDictionary();
        int projectionCount = 0;

        ImmutableDictionary<int, object> current =
            ConsoleSnapshotState.UpdateProjection(
                previous,
                [2],
                _ =>
                {
                    projectionCount++;
                    return new object();
                });

        Assert.Equal(1, projectionCount);
        Assert.Same(previous[1], current[1]);
        Assert.NotSame(previous[2], current[2]);
        Assert.Same(previous[3], current[3]);
    }

}
