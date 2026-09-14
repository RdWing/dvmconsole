// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.Storage;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PreparedLifecycleTests
{
    [Fact]
    public async Task RadioAuthorityAndSubscriberLifecycleOperateWithoutViewsAndDetachOnRetirement()
    {
        string directory = Path.Combine(Path.GetTempPath(), "neo-prepared-events", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configuration = new ConsoleConfiguration
        {
            Systems = [new() { Name = "Test", Identity = "Test", Address = "127.0.0.1", Port = 62031, PeerId = 1, Rid = "1001" }],
            Zones = [new() { Name = "Operations", Channels = [new() { Name = "Dispatch", System = "Test", Mode = "p25", Tgid = "100" }] }]
        };
        var state = ConsoleSessionState.Create(configuration);
        var services = new ConsoleSessionServices();
        var runtime = ConsoleOperationalRuntime.Prepare(services, state);
        var radio = new Radio(runtime.TransmitChannels.CaptureAll());
        var dependencies = new DesktopRuntimeDependencies(new UserSettingsStore(Path.Combine(directory, "settings.json")),
            () => [], (_, _) => throw new InvalidOperationException("No hardware PTT during preparation."),
            ImmediateTestUiDispatcher.Instance, new ManagedAssetStore(Path.Combine(directory, "assets")),
            new UnusedAudioFactory(), new NativeVocoderFactory(), NetworkDisabledDemo: true);
        try
        {
            var prepared = new MainWindowViewModel.PreparedLiveSession(runtime, state,
                new UserSettings { RecordingRootPath = Path.Combine(directory, "recordings") }, dependencies, services);
            MainWindowViewModel.RegisterSessionOwnership(services, prepared);
            prepared.InitializeMedia([radio], dependencies, new TransmitKeyPort(null, null, null), false);
            Assert.Null(prepared.ViewModel);
            var channel = Assert.Single(runtime.Channels.Values);
            radio.PublishAuthority(TargetAuthorityState.Unavailable);
            Assert.Equal(TargetAuthorityState.Unavailable, prepared.Snapshots.Capture().Channels[channel.Id].Authority);
            Assert.Contains("PTT disabled", state.Status.Snapshot.Transmit);

            Assert.True(prepared.SubscriberCommands.Submit(radio.SystemId, radio, radio, ConsoleSubscriberCommand.RadioCheck, 123).Submitted);
            radio.Acknowledge(123);
            Assert.Equal(ConsoleSubscriberAcknowledgementState.Received, Assert.Single(prepared.SubscriberCommands.History).Acknowledgement);
            Assert.True(prepared.SubscriberCommands.Submit(radio.SystemId, radio, radio, ConsoleSubscriberCommand.RadioCheck, 124).Submitted);
            radio.SetConnected(false);
            Assert.Equal(ConsoleSubscriberAcknowledgementState.Interrupted,
                prepared.SubscriberCommands.History.Single(result => result.DestinationId == 124).Acknowledgement);
            Assert.Contains("Disconnected", state.Status.Snapshot.Console);
            var history = Assert.Single(state.History.Snapshot);
            Assert.Equal(ConsoleCallDirection.Event, history.Direction);
            Assert.Equal("Test disconnected", history.EventMessage);
            Assert.Equal("1001", history.EventRid);
            radio.SetConnected(false);
            Assert.Single(state.History.Snapshot);

            await services.DisposeAsync();
            radio.PublishAuthority(TargetAuthorityState.Available);
            Assert.Equal(TargetAuthorityState.Unavailable, channel.Authority);
        }
        finally
        {
            await services.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Radio(IReadOnlyList<TransmitChannelDescriptor> channels) : IRadioSession,
        IRadioSubscriberCommandEndpoint, IRadioSubscriberAcknowledgementSource, IRadioConnectionStateNotifications
    {
        public SystemId SystemId { get; } = SystemId.FromName("Test");
        public string Name => "Test";
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => channels.ToArray();
        public IReadOnlyCollection<ChannelId> ChannelIds => channels.Select(channel => channel.Id).ToArray();
        public bool IsConnected { get; private set; } = true;
        public bool IsConnectionActive => IsConnected;
        public uint? SourceId => 1001;
        public RadioConnectionSnapshot ConnectionState => new(SystemId, Name,
            IsConnected ? RadioConnectionState.Connected : RadioConnectionState.Disconnected, "Fixture", DateTimeOffset.UtcNow);
        public event EventHandler? ConnectionStateChanged;
        public event EventHandler<ConsoleSubscriberAcknowledgement>? SubscriberAcknowledged;
        public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged;
        public event EventHandler<RadioTrafficRecord>? TrafficReceived { add { } remove { } }
        public void SetConnected(bool connected) { IsConnected = connected; ConnectionStateChanged?.Invoke(this, EventArgs.Empty); }
        public void PublishAuthority(TargetAuthorityState value) => AuthorityChanged?.Invoke(this,
            new(SystemId, channels.Select(channel => new TalkgroupAuthorityChannelRecord(channel.Id, value, null)).ToArray(), DateTimeOffset.UtcNow));
        public void Acknowledge(uint subscriber) => SubscriberAcknowledged?.Invoke(this, new(SystemId, ConsoleSubscriberCommand.RadioCheck, subscriber));
        public void SendSubscriberCommand(ConsoleSubscriberCommand command, uint destinationId) { }
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot) => TargetAuthorityState.Available;
        public uint CreateStreamId() => 1;
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId) { }
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask QuiesceAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedAudioFactory : IAudioBackendFactory
    {
        public IAudioBackend Create(AudioBackendConfiguration configuration) => throw new InvalidOperationException("No audio requested.");
    }
}
