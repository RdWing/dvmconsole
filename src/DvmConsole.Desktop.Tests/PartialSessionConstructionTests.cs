// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PartialSessionConstructionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReplacementReleasesTransmitBeforePublicationAndRestoresTheRealConnection(bool generatedTone)
    {
        string root = Path.Combine(Path.GetTempPath(), "neo-rollback-connection", Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
        var options = new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null)
        { SourceId = 1001 };
        var channel = new ChannelViewModel(new ChannelConfiguration
        { Name = "Dispatch", System = "Test", Mode = generatedTone ? "analog" : "p25", Tgid = "100" });
        var radio = new TrackingRadio(options, channel);
        var system = new SystemViewModel(options, "Test", "local", [channel], [], 0, new Factory(radio), false);
        var initial = new MainWindowViewModel("Initial", [system], [], DesktopTestSessionBuilder.CreateOptions(store));
        var replacement = new MainWindowViewModel("Replacement", [], [], DesktopTestSessionBuilder.CreateOptions(store));
        int publications = 0;
        bool? transmittingAtPublication = null;
        bool? sendingToneAtPublication = null;
        var host = new MainWindowSessionHost(initial, (_, _) => { }, _ =>
        {
            if (++publications > 1)
            {
                transmittingAtPublication = channel.IsTransmitting;
                sendingToneAtPublication = initial.OperationalRuntime.Transmit.Tones.IsSending;
                throw new IOException("Publication rejected");
            }
        }, () => { }, () => { });
        Task<Exception?>? tone = null;
        try
        {
            if (generatedTone)
            {
                tone = initial.OperationalRuntime.Transmit.GeneratedOperation.SendAsync(
                    [new(channel.ToTransmitDescriptor(), radio)], new short[80_000]);
                await Task.WhenAny(tone, radio.TrafficStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
                if (tone.IsCompleted) await tone;
                Assert.True(radio.TrafficStarted.Task.IsCompleted);
            }
            else
            {
                channel.SetTransmitEnabled(true, streamId: 42);
                await host.ChannelPtt.PressAsync(channel.Id);
            }
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReplaceAsync(replacement));
            Assert.DoesNotContain("Restoring the previous session also failed", failure.Message);
            Assert.Same(initial, host.ViewModel);
            Assert.False(initial.IsSessionInputSuppressed);
            Assert.Equal(1, radio.StartCount);
            Assert.True(radio.IsConnectionActive);
            Assert.False(channel.IsTransmitting);
            Assert.False(transmittingAtPublication);
            Assert.False(sendingToneAtPublication);
            if (tone is not null)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tone);
        }
        finally
        {
            await host.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledLoadDoesNotCreateSettingsOrRuntimeStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), "neo-cancelled-load", Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MainWindowViewModel.LoadAsync(
            null, store, cancellationToken: new CancellationToken(true)));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task AuthorityStateDoesNotWaitForDesktopPresentation()
    {
        string root = Path.Combine(Path.GetTempPath(), "neo-authority-dispatch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dispatcher = new HeldPresentationDispatcher();
        try
        {
            var options = new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null);
            var channel = new ChannelViewModel(new ChannelConfiguration
            { Name = "Dispatch", System = "Test", Mode = "p25", Tgid = "100" });
            var radio = new TrackingRadio(options, channel);
            var system = new SystemViewModel(options, "Test", "local", [channel], [], 0, new Factory(radio), false);
            var store = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
            store.Save(new UserSettings { RecordingRootPath = Path.Combine(root, "recordings") });
            await using var owner = new MainWindowViewModel("Ready", [system], [],
                new MainWindowViewModelOptions(Document: new(store),
                    Host: new(UiDispatcher: dispatcher, SerialPortProvider: () => [])));
            dispatcher.Hold = true;
            try
            {
                radio.PublishAuthority(new(radio.SystemId,
                    [new(channel.Id, TargetAuthorityState.Unavailable, null)], DateTimeOffset.UtcNow));
                Assert.Equal(TargetAuthorityState.Unavailable, channel.SessionState.Authority);
                Assert.Contains("PTT disabled", owner.SessionStatus.Snapshot.Console);
                Assert.True(dispatcher.Pending.Count > 0);
            }
            finally { dispatcher.Hold = false; dispatcher.Flush(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectStopsOnlyCallsOwnedByThatSystemBeforePresentationRuns(bool ownsCall)
    {
        string root = Path.Combine(Path.GetTempPath(), "neo-disconnect-dispatch", Guid.NewGuid().ToString("N"));
        var dispatcher = new HeldPresentationDispatcher();
        try
        {
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "UserSettings.json")),
                uiDispatcher: dispatcher, networkDisabledDemo: true);
            var system = owner.Systems[0];
            var channel = system.Channels[0];
            var other = owner.Systems[1].Channels[0];
            owner.OperationalRuntime.ObserveConnectionState(system.Id, system.Name, RadioConnectionState.Connected);
            owner.OperationalRuntime.JitterEffectiveness.Observe(system.Name, new ReceiveWorkItemTiming(
                new FneTrafficFrame(FneTrafficProtocol.P25, 1, 2, 100, 0, "GROUP", "VOICE", "VOICE", 1, 10, []),
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
                JitterBufferReorderedPacket: true));
            channel.SessionState.SetTransmitEnabled(ownsCall, 77);
            other.SessionState.SetTransmitEnabled(true, 78);
            dispatcher.Hold = true;
            try
            {
                owner.HandleSystemStatus(system, new(system.Name, FneConnectionState.Disconnected, "Connection lost", DateTimeOffset.UtcNow));
                Assert.Equal(default, owner.OperationalRuntime.JitterEffectiveness.GetSnapshot(system.Name));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (ownsCall && (channel.IsTransmitting || other.IsTransmitting))
                    await Task.Delay(10, timeout.Token);
                Assert.False(channel.IsTransmitting);
                Assert.Equal(!ownsCall, other.IsTransmitting);
                Assert.Contains("Disconnected", owner.SessionStatus.Snapshot.Console);
                Assert.True(dispatcher.Pending.Count > 0);
            }
            finally { dispatcher.Hold = false; dispatcher.Flush(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class HeldPresentationDispatcher : IUiDispatcher
    {
        public bool Hold;
        public System.Collections.Concurrent.ConcurrentQueue<Action> Pending { get; } = new();
        public bool CheckAccess() => !Hold;
        public void Post(Action action, bool background = false) => Pending.Enqueue(action);
        public ValueTask InvokeAsync(Action action) { action(); return ValueTask.CompletedTask; }
        public void Flush() { while (Pending.TryDequeue(out var action)) action(); }
    }

    [Fact]
    public async Task DirectShellFailureRetiresPreparedGraphBeforeReturning()
    {
        string root = Path.Combine(Path.GetTempPath(), "neo-shell-failure", Guid.NewGuid().ToString("N"));
        var store = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
        var radioOptions = new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null);
        var channel = new ChannelViewModel(new ChannelConfiguration
        { Name = "Dispatch", System = "Test", Mode = "p25", Tgid = "100" });
        var radio = new TrackingRadio(radioOptions, channel);
        var system = new SystemViewModel(radioOptions, "Test", "local", [channel], [], 0, new Factory(radio), false);
        await using var services = new ConsoleSessionServices();
        var options = DesktopTestSessionBuilder.CreateOptions(store, services);
        var failure = new ArgumentException("Host device provider failed during shell attachment.");
        try
        {
            ArgumentException reported = Assert.Throws<ArgumentException>(() => new MainWindowViewModel("Ready", [system], [],
                options with { Host = options.Host! with { SerialPortProvider = () => throw failure } }));
            Assert.Same(failure, reported);
            Assert.Equal(0, radio.StartCount);
            Assert.Equal(1, radio.DisposeCount);
            TargetAuthorityState initial = channel.SessionState.Authority;
            radio.PublishAuthority(new(radio.SystemId,
                [new(channel.Id, TargetAuthorityState.Unavailable, "Retired")], DateTimeOffset.UtcNow));
            Assert.Equal(initial, channel.SessionState.Authority);
            await services.DisposeAsync();
            Assert.Equal(1, radio.DisposeCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LaterSystemConstructionFailureDisposesEarlierSystemsExactlyOnce()
    {
        var options = new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null);
        var channel = new ChannelViewModel(new ChannelConfiguration { Name = "Dispatch", System = "Test", Mode = "p25", Tgid = "100" });
        var radio = new TrackingRadio(options, channel);
        var system = new SystemViewModel(options, "Test", "local", [channel], [], 0,
            new Factory(radio), false);
        await using var services = new ConsoleSessionServices();
        IEnumerable<SystemViewModel> ConstructSystems()
        {
            yield return system;
            throw new InvalidDataException("The second system could not be constructed.");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => ConsoleSessionConstruction.CreateAsync(
            services, _ => ValueTask.FromResult(new MainWindowViewModel("", ConstructSystems(), [],
                new MainWindowViewModelOptions(Host: new(SessionServices: services))))).AsTask());
        Assert.Equal(1, radio.DisposeCount);
        await services.DisposeAsync();
        Assert.Equal(1, radio.DisposeCount);
    }

    [Fact]
    public async Task ViewDisposalLeavesPreparedRadioWithItsApplicationOwner()
    {
        var options = new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null);
        var channel = new ChannelViewModel(new ChannelConfiguration { Name = "Dispatch", System = "Test", Mode = "p25", Tgid = "100" });
        var radio = new TrackingRadio(options, channel);
        await using var owner = new ConsoleSessionServices();
        owner.Connection.OwnAsync("radio", radio);
        var view = new SystemViewModel(options, "Test", "local", [channel], [], 0, null, false, radio);
        await view.DisposeAsync();
        Assert.Equal(0, radio.DisposeCount);
        await owner.DisposeAsync();
        Assert.Equal(1, radio.DisposeCount);
    }

    private sealed class Factory(TrackingRadio radio) : IFneRadioSessionFactory
    {
        public RadioSystemDescriptor Descriptor => new(radio.SystemId, radio.Name, "FNE", new Dictionary<string, string>());
        public IFneRadioSession Create() => radio;
    }

    private sealed class TrackingRadio(
        FneConnectionOptions options,
        ChannelViewModel channel) : IFneRadioSession
    {
        public SystemId SystemId { get; } = SystemId.FromName(options.Name);
        public string Name => options.Name;
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors
            => [channel.ToTransmitDescriptor()];
        public IReadOnlyCollection<ChannelId> ChannelIds
            => [new ChannelId(channel.SessionId)];
        public bool IsConnected => IsConnectionActive;
        public bool IsConnectionActive { get; private set; } = true;
        public int StartCount { get; private set; }
        public TaskCompletionSource TrafficStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint? SourceId => options.SourceId;
        public string Identity => options.Identity;
        public FneConnectionStatus Status { get; } = new(
            options.Name,
            FneConnectionState.Connected,
            "Connected for test",
            DateTimeOffset.UtcNow);

        public event EventHandler<RadioTrafficRecord>? TrafficReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged;
        public void PublishAuthority(TalkgroupAuthorityRecord authority) => AuthorityChanged?.Invoke(this, authority);

        public event EventHandler<FneConnectionStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<FneLogEntry>? LogReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<FneKeyResponse>? KeyResponseReceived
        {
            add { }
            remove { }
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            IsConnectionActive = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask QuiesceAsync(CancellationToken cancellationToken = default)
        {
            IsConnectionActive = false;
            return ValueTask.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Abort() { }

        public void SetVerboseLogging(bool enabled)
        {
        }

        public FneTalkgroupAvailability GetTalkgroupAvailability(
            FneTrafficProtocol protocol,
            uint destinationId,
            byte runtimeSlot)
            => FneTalkgroupAvailability.Available;

        public uint CreateStreamId() => 42;

        public void SendTraffic(
            RadioMediaProtocol protocol,
            ReadOnlyMemory<byte> payload,
            ushort packetSequence,
            uint streamId)
        {
            TrafficStarted.TrySetResult();
        }

        public void SendTraffic(
            FneTrafficProtocol protocol,
            ReadOnlyMemory<byte> payload,
            ushort packetSequence,
            uint streamId)
        {
        }

        public TargetAuthorityState GetTargetAuthority(
            RadioMediaProtocol protocol,
            uint destinationId,
            byte runtimeSlot)
            => TargetAuthorityState.Available;

        public void RequestP25Key(byte algorithmId, ushort keyId)
        {
        }

        public void SendP25SubscriberCommand(P25SubscriberCommand command, uint destinationId)
        {
        }

        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
