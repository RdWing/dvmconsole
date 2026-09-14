// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class TransmitAudioTransitionControllerTests
{
    [Fact]
    public async Task RestoreUsesCurrentVolumeAndPreservesOperatorMute()
    {
        var host = new Host();
        var controller = host.CreateController();
        await controller.MuteReceiveAudioAsync("Transmitting");
        Assert.True(host.State.AudioSuspended);
        host.Volume = 0.25;
        host.Muted = true;

        await controller.RestoreSuspendedAudioAsync();

        Assert.False(host.State.AudioSuspended);
        Assert.Equal([false, false], host.PlaybackChanges);
        Assert.Equal(0.25, host.AppliedGain);
        Assert.Equal(0, host.StartCalls);
    }

    [Fact]
    public async Task FailedRestorationRetainsSuspensionForRetry()
    {
        var host = new Host();
        var controller = host.CreateController();
        await controller.MuteReceiveAudioAsync("Transmitting");
        host.FailGain = true;

        await Assert.ThrowsAsync<IOException>(controller.RestoreSuspendedAudioAsync);
        Assert.True(host.State.AudioSuspended);
        host.FailGain = false;
        await controller.RestoreSuspendedAudioAsync();

        Assert.False(host.State.AudioSuspended);
        Assert.Equal([false, true], host.PlaybackChanges);
    }

    [Fact]
    public async Task LostRouteRestartsOnceAndClearsSuspensionAfterSuccess()
    {
        var host = new Host();
        var controller = host.CreateController();
        await controller.MuteReceiveAudioAsync("Transmitting");
        host.Active = false;

        await controller.RestoreSuspendedAudioAsync();
        await controller.RestoreSuspendedAudioAsync();

        Assert.False(host.State.AudioSuspended);
        Assert.Equal(1, host.StartCalls);
    }

    private sealed class Host : ITransmitReceiveRoutePort, ITransmitReceiveMutePort,
        ITransmitPermitTonePort, ITransmitAudioPresentationPort, ITransmitAudioGate
    {
        private readonly ChannelId id = new(new ChannelSessionId("System", ChannelProtocol.Dmr, 100, 0, "Dispatch"));
        public Host() => State = new(new(id, new ChannelRuntimeDefinition("Dispatch", "System", "dmr", 100, 0)), true, false, false);
        public ReceiveOutputChannelState State { get; private set; }
        public bool Active { get; set; } = true;
        public bool Muted { get; set; }
        public bool FailGain { get; set; }
        public double Volume { get; set; } = 1;
        public double AppliedGain { get; private set; }
        public int StartCalls { get; private set; }
        public List<bool> PlaybackChanges { get; } = [];
        public TransmitAudioTransitionController CreateController() => new(this, this, this, this, this);
        public IReadOnlyList<ChannelId> LivePlaybackChannels => Active ? [id] : [];
        public long SetLivePlaybackDiscarded(bool discarded) => 0;
        public bool IsActive(ChannelId channelId) => Active;
        public Task SetGainAsync(ChannelId channelId, double gain)
        {
            if (FailGain) throw new IOException("route unavailable");
            AppliedGain = gain;
            return Task.CompletedTask;
        }
        public Task SetLivePlaybackEnabledAsync(ChannelId channelId, bool enabled)
        {
            PlaybackChanges.Add(enabled);
            return Task.CompletedTask;
        }
        public Task StartAsync(ChannelId channel)
        {
            Active = true;
            StartCalls++;
            return Task.CompletedTask;
        }
        public bool ShouldEnableLivePlayback(ChannelId channel, bool isTemporarilySuspended) => !Muted && !isTemporarilySuspended;
        public Task<LocalTonePlaybackResult> PlayAsync(LocalTonePlaybackRequest request) => throw new NotSupportedException();
        public Task<LocalTonePlaybackResult> PlayTalkPermitAsync(bool microphoneStartedCold, bool? microphoneIsBluetooth) => throw new NotSupportedException();
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
        public ReceiveOutputChannelState Capture(ChannelId channelId) => State;
        public void SetAudioSuspended(ChannelId channelId, bool suspended) => State = State with { AudioSuspended = suspended };
        public double GetVolume(ChannelId channel) => Volume;
        public Task RunAsync(Action action) { action(); return Task.CompletedTask; }
        public Task RunAsync(Func<Task> operation) => operation();
        public void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message) { }
        public void PublishStatus(string text) { }
    }
}
