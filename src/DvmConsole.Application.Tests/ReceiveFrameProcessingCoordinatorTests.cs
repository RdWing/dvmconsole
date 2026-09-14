// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ReceiveFrameProcessingCoordinatorTests
{
    [Fact]
    public async Task RecordingWaitsForItsDecoderBeforeProcessingTheQueuedFrame()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host { Recording = true, Ready = ready.Task };
        Task pending = host.Coordinator.ProcessAsync(default, new Frame(), null, CancellationToken.None);
        Assert.Equal(["ensure"], host.Events);
        Assert.False(pending.IsCompleted);
        ready.SetResult();
        await pending;
        Assert.Equal(["ensure", "process", "diagnostics", "recording"], host.Events);
    }

    [Fact]
    public async Task OutputRecoveryDoesNotReplayTheInterruptedFrame()
    {
        var host = new Host { Active = true, Failure = new IOException("Output removed") };
        await host.Coordinator.ProcessAsync(default, new Frame(), null, CancellationToken.None);
        Assert.Equal(["process", "recover", "recording"], host.Events);
    }

    [Fact]
    public async Task ExistingRecoveryRemainsTheOnlyOwner()
    {
        var host = new Host { Active = true, Recovering = true, Failure = new IOException("Output removed") };
        await host.Coordinator.ProcessAsync(default, new Frame(), null, CancellationToken.None);
        Assert.Equal(["process", "recording"], host.Events);
    }

    [Fact]
    public async Task CancellationStillRetiresATerminatorWithoutTreatingItAsADeviceFault()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var host = new Host { Active = true, Failure = new OperationCanceledException(cancellation.Token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Coordinator.ProcessAsync(
            default, new Frame(true), null, cancellation.Token));
        Assert.Equal(["process", "end"], host.Events);
    }

    [Fact]
    public async Task InactiveAudioStillRetiresTheTerminatingStream()
    {
        var host = new Host();
        await host.Coordinator.ProcessAsync(default, new Frame(true), null, CancellationToken.None);
        Assert.Equal(["end"], host.Events);
    }

    [Fact]
    public async Task NonDeviceFailureStopsTheChannelAndStillObservesRecordingTraffic()
    {
        var host = new Host { Active = true, Failure = new ArgumentException("Invalid media") };
        await host.Coordinator.ProcessAsync(default, new Frame(), null, CancellationToken.None);
        Assert.Equal(["process", "fault", "stop", "recording"], host.Events);
    }

    private sealed class Host : IReceiveFrameAudioPort, IReceiveFrameObservationPort, IReceiveRecordingTrafficPort
    {
        public List<string> Events { get; } = [];
        public bool Recording;
        public bool Active;
        public bool Recovering;
        public Task Ready = Task.CompletedTask;
        public Exception? Failure;
        public ReceiveFrameProcessingCoordinator Coordinator => new(this, this, SystemClock.Instance, this);
        public bool IsRecordingEnabled(ChannelId channel) => Recording;
        public bool IsActive(ChannelId channel) => Active;
        public async Task EnsureRecordingAudioAsync(ChannelId channel, CancellationToken cancellationToken)
        {
            Events.Add("ensure");
            await Ready.WaitAsync(cancellationToken);
            Active = true;
        }
        public Task<ReceiveAudioProcessTiming> ProcessAsync(ChannelId channel, IRadioMediaFrame traffic,
            RadioFrameEncryption? encryption, CancellationToken cancellationToken)
        {
            Events.Add("process");
            return Failure is null ? Task.FromResult(default(ReceiveAudioProcessTiming)) : Task.FromException<ReceiveAudioProcessTiming>(Failure);
        }
        public bool IsDeviceFailure(Exception exception) => exception is IOException;
        public void RequestRecovery(ChannelId channel, Exception failure)
        {
            if (!Recovering) Events.Add("recover");
        }
        public Task StopAsync(ChannelId channel, CancellationToken cancellationToken) { Events.Add("stop"); return Task.CompletedTask; }
        public void PublishDiagnostics(ChannelId channel, uint streamId, DateTimeOffset now) => Events.Add("diagnostics");
        public void ObserveRecovery(TimeSpan elapsed, ReceiveRouteRecoveryResult recovery) => Events.Add("recovery-observed");
        public void ShowRecovery(ReceiveRouteRecoveryResult recovery) => Events.Add("recovery-shown");
        public void ShowFault(ChannelId channel, Exception exception) => Events.Add("fault");
        public void EndStream(ChannelId channel, uint streamId) => Events.Add("end");
        public void ObserveRecordingTraffic(ChannelId channel, IRadioMediaFrame traffic) => Events.Add("recording");
    }

    private sealed class Frame(bool terminator = false) : IRadioMediaFrame
    {
        public RadioMediaProtocol Protocol => RadioMediaProtocol.P25;
        public uint PeerId => 1;
        public uint SourceId => 42;
        public uint DestinationId => 100;
        public byte? Slot => null;
        public string CallType => "GROUP";
        public string FrameType => terminator ? "TERMINATOR" : "VOICE";
        public string Subtype => terminator ? "TDU" : "LDU1";
        public ushort PacketSequence => 1;
        public uint StreamId => 10;
        public byte[] Payload => [];
    }
}
