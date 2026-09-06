// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class TransmitLifecycleCoordinatorTests
{
    [Fact]
    public async Task MicrophoneWarmsImmediatelyButAudioWaitsForReadinessCueAndRecovery()
    {
        var rig = new Rig();
        Task start = rig.Coordinator.StartAsync(rig.Request(true));
        await rig.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(rig.Suppressed);
        Assert.True(rig.CaptureStarted);
        Assert.False(rig.Activated.Task.IsCompleted);

        rig.Ready.SetResult(new(TimeSpan.Zero, TimeSpan.Zero));
        await rig.CueStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.Presented.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(rig.Suppressed);
        Assert.False(rig.ReleaseEntered.Task.IsCompleted);

        rig.CueFinished.SetResult();
        await rig.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromMilliseconds(60), rig.Guard);
        Assert.True(rig.RequireFreshCallback);
        Assert.True(rig.Suppressed);
        rig.Recovered.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rig.Suppressed);
        Assert.Null(rig.StartFailure);
    }

    [Fact]
    public async Task WithoutPermitToneReadinessStillGatesActivationAndAddsNoToneGuard()
    {
        var rig = new Rig();
        Task start = rig.Coordinator.StartAsync(rig.Request(false));
        await rig.ReadinessEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rig.Activated.Task.IsCompleted);
        rig.Ready.SetResult(new(TimeSpan.Zero, TimeSpan.Zero));
        await rig.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rig.Prepared.Task.IsCompleted);
        Assert.Equal(TimeSpan.Zero, rig.Guard);
        Assert.False(rig.RequireFreshCallback);
        rig.Recovered.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(rig.Activated.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("readiness")]
    [InlineData("activation")]
    [InlineData("cue")]
    [InlineData("recovery")]
    public async Task StartupFailuresStopTransportAndRestoreReceive(string phase)
    {
        var rig = new Rig { FailurePhase = phase, ActiveMicrophoneStartedCold = true, ActiveMicrophoneIsBluetooth = true };
        rig.Ready.SetResult(new(TimeSpan.Zero, TimeSpan.Zero));
        rig.CueFinished.SetResult();
        rig.Recovered.SetResult();
        await rig.Coordinator.StartAsync(rig.Request(true)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<IOException>(rig.StartFailure);
        Assert.Empty(rig.ActiveChannels);
        Assert.Equal(1, rig.StopCalls);
        Assert.Equal(1, rig.RestoreCalls);
        Assert.Equal(1, rig.GatesOpened);
        Assert.Equal(1, rig.GatesClosed);
        Assert.False(rig.Suppressed);
    }

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, true, false, 1)]
    [InlineData(true, null, false, 1)]
    [InlineData(true, true, true, 0)]
    public async Task ReceiveTransitionUsesActualMicrophoneRoute(bool cold, bool? bluetooth, bool mute, int gates)
    {
        var rig = new Rig
        {
            ActiveMicrophoneStartedCold = cold,
            ActiveMicrophoneIsBluetooth = bluetooth,
            MuteReceiveWhileTransmitting = mute
        };
        rig.Ready.SetResult(new(TimeSpan.Zero, TimeSpan.Zero));
        rig.Recovered.SetResult();
        await rig.Coordinator.StartAsync(rig.Request(false)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(gates, rig.GatesOpened);
        Assert.Equal(gates, rig.GatesClosed);
        Assert.Equal(mute ? 1 : 0, rig.MuteCalls);
        Assert.Equal(cold && bluetooth != false, rig.RequireFreshCallback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedStopKeepsOwnershipUntilRetry(bool propagate)
    {
        var rig = new Rig { UnconfirmedStop = true };
        rig.SetActive();
        if (propagate)
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Coordinator.StopAsync(rig.ActiveChannels, propagateUnconfirmedStop: true));
        else
            await rig.Coordinator.StopAsync(rig.ActiveChannels);
        Assert.Single(rig.Unresolved);
        Assert.Equal(0, rig.ClearCalls);
        Assert.Equal(0, rig.RestoreCalls);
        rig.UnconfirmedStop = false;
        await rig.Coordinator.StopAsync(rig.ActiveChannels);
        Assert.Empty(rig.Unresolved);
        Assert.Equal(1, rig.ClearCalls);
        Assert.Equal(1, rig.RestoreCalls);
    }

    [Fact]
    public async Task ReleaseGatesMicrophoneBeforeWaitingForPresentation()
    {
        var rig = new Rig { HoldStoppingPresentation = true };
        rig.SetActive();
        Task stop = rig.Coordinator.StopAsync(rig.ActiveChannels);
        await rig.StoppingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(rig.Suppressed);
            Assert.Equal(0, rig.StopCalls);
        }
        finally
        {
            rig.AllowStoppingPresentation.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, rig.StopCalls);
    }

    [Fact]
    public async Task ReleaseDuringPermitCueRetainsInputOrderingThroughTheExtractedLifecycle()
    {
        var rig = new Rig();
        rig.Ready.SetResult(new(TimeSpan.Zero, TimeSpan.Zero));
        rig.Recovered.SetResult();
        var card = new ChannelViewModel(new ChannelConfiguration { Name = "PTT", System = "Training", Tgid = "100" });
        var input = new CardPttController(
            async _ =>
            {
                await rig.Coordinator.StartAsync(rig.Request(true));
                return rig.ActiveChannels.Count > 0;
            },
            _ => rig.Coordinator.StopAsync(rig.ActiveChannels));
        Task press = input.PressAsync(card);
        await rig.CueStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task release = input.ReleaseAsync(card);
        Assert.False(release.IsCompleted);
        Assert.Equal(0, rig.StopCalls);
        rig.CueFinished.SetResult();
        await Task.WhenAll(press, release).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(rig.ActiveChannels);
        Assert.Equal(1, rig.StopCalls);
        Assert.Equal(1, rig.RestoreCalls);
    }

    private sealed class Rig : ITransmitLifecycleTransport, ITransmitLifecycleAudio, ITransmitLifecyclePresentation
    {
        private readonly TransmitTarget target;
        public Rig()
        {
            var channel = new ChannelViewModel(new ChannelConfiguration { Name = "Dispatch", System = "Training", Tgid = "100", Mode = "p25" });
            TransmitChannelDescriptor descriptor = channel.ToTransmitDescriptor();
            target = new TransmitTarget(descriptor, new Endpoint(descriptor));
            Coordinator = new(this, this, this);
        }
        public TransmitLifecycleCoordinator Coordinator { get; }
        public TransmitStartRequest Request(bool tone) => new([target], tone);
        public TaskCompletionSource Prepared { get; } = Barrier();
        public TaskCompletionSource ReadinessEntered { get; } = Barrier();
        public TaskCompletionSource<MicrophoneReadinessTiming> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Activated { get; } = Barrier();
        public TaskCompletionSource CueStarted { get; } = Barrier();
        public TaskCompletionSource CueFinished { get; } = Barrier();
        public TaskCompletionSource Presented { get; } = Barrier();
        public TaskCompletionSource ReleaseEntered { get; } = Barrier();
        public TaskCompletionSource Recovered { get; } = Barrier();
        public IReadOnlyList<ChannelId> ActiveChannels { get; private set; } = [];
        public bool ActiveMicrophoneStartedCold { get; set; }
        public bool? ActiveMicrophoneIsBluetooth { get; set; } = false;
        public bool? SelectedMicrophoneIsBluetooth => ActiveMicrophoneIsBluetooth;
        public bool MuteReceiveWhileTransmitting { get; set; }
        public bool CaptureStarted { get; private set; }
        public bool Suppressed { get; private set; }
        public bool RequireFreshCallback { get; private set; }
        public TimeSpan Guard { get; private set; }
        public string? FailurePhase { get; set; }
        public Exception? StartFailure { get; private set; }
        public bool UnconfirmedStop { get; set; }
        public IReadOnlySet<ChannelId> Unresolved { get; private set; } = new HashSet<ChannelId>();
        public int StopCalls { get; private set; }
        public bool HoldStoppingPresentation { get; init; }
        public TaskCompletionSource StoppingEntered { get; } = Barrier();
        public TaskCompletionSource AllowStoppingPresentation { get; } = Barrier();
        public int RestoreCalls { get; private set; }
        public int ClearCalls { get; private set; }
        public int GatesOpened { get; private set; }
        public int GatesClosed { get; private set; }
        public int MuteCalls { get; private set; }
        public void SetActive() => ActiveChannels = [target.Channel.Id];
        public uint GetActiveStreamId(ChannelId channel) => ActiveChannels.Contains(channel) ? 42u : 0u;
        public Task<MicrophoneStartExpectation> InspectNextMicrophoneStartAsync(bool? inputIsBluetooth)
            => Task.FromResult(new MicrophoneStartExpectation(ActiveMicrophoneStartedCold, inputIsBluetooth));
        public void SetMicrophoneAudioSuppressed(bool suppressed) => Suppressed = suppressed;
        public Task StartAsync(IReadOnlyList<TransmitTarget> targets)
        {
            Assert.True(Suppressed);
            CaptureStarted = true;
            ActiveChannels = targets.Select(target => target.Channel.Id).ToArray();
            return Task.CompletedTask;
        }
        public async Task<MicrophoneReadinessTiming> WaitForMicrophoneReadyAsync()
        {
            ReadinessEntered.TrySetResult();
            await Ready.Task;
            Fail("readiness");
            return await Ready.Task;
        }
        public Task ActivateAsync(CancellationToken cancellationToken = default)
        {
            Fail("activation");
            Activated.TrySetResult();
            return Task.CompletedTask;
        }
        public async Task<TimeSpan> ReleaseMicrophoneAudioAsync(bool requireFreshRecoveryCallback, TimeSpan postCueSuppressionDuration)
        {
            RequireFreshCallback = requireFreshRecoveryCallback;
            Guard = postCueSuppressionDuration;
            ReleaseEntered.TrySetResult();
            await Recovered.Task;
            Fail("recovery");
            Suppressed = false;
            return TimeSpan.Zero;
        }
        public Task StopAsync()
        {
            StopCalls++;
            if (UnconfirmedStop)
                throw new IOException("unconfirmed stop");
            ActiveChannels = [];
            return Task.CompletedTask;
        }
        public Task MuteReceiveAudioAsync(string statusText) { MuteCalls++; return Task.CompletedTask; }
        public long BeginReceiveTransition() { GatesOpened++; return 10; }
        public Task EndColdBluetoothReceiveTransitionAsync(long discardedAtStart)
        {
            Assert.Equal(10, discardedAtStart);
            GatesClosed++;
            return Task.CompletedTask;
        }
        public async Task<LocalTonePlaybackResult> PreparePermitToneAsync(
            bool microphoneStartedCold, bool? microphoneIsBluetooth, Task cueReleaseBarrier,
            Func<CancellationToken, Task> beforeCueAsync)
        {
            Prepared.TrySetResult();
            await cueReleaseBarrier;
            await beforeCueAsync(CancellationToken.None);
            CueStarted.TrySetResult();
            await CueFinished.Task;
            Fail("cue");
            return new(new AudioDeviceInfo("output", "Test", AudioDirection.Output, true, false),
                1, 1, 1, null, TimeSpan.Zero, default,
                new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
                    TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        }
        public async Task CompletePermitToneAsync(Task<LocalTonePlaybackResult> playback, TransmitStartupDiagnostics diagnostics, Stopwatch timer)
            => await playback;
        public Task RestoreSuspendedAudioAsync() { RestoreCalls++; return Task.CompletedTask; }
        public Task StartedAsync(IReadOnlyList<TransmitTarget> targets, IReadOnlyList<ChannelId> activeChannels,
            TransmitStartupDiagnostics diagnostics, Stopwatch timer)
        {
            Assert.True(Activated.Task.IsCompletedSuccessfully);
            Assert.True(Suppressed);
            Presented.TrySetResult();
            return Task.CompletedTask;
        }
        public Task StartFailedAsync(IReadOnlyList<ChannelId> channels, Exception failure)
        {
            Assert.Equal(target.Channel.Id, Assert.Single(channels));
            StartFailure = failure;
            return Task.CompletedTask;
        }
        public Task StoppingAsync(IReadOnlyList<TransmitStream> streams)
        {
            Assert.Equal(42u, Assert.Single(streams).StreamId);
            StoppingEntered.TrySetResult();
            return HoldStoppingPresentation ? AllowStoppingPresentation.Task : Task.CompletedTask;
        }
        public Task StoppedAsync(IReadOnlyList<ChannelId> channels, IReadOnlyList<TransmitStream> streams,
            IReadOnlySet<ChannelId> unresolved, TimeSpan elapsed, Exception? failure, string? statusText)
        {
            Unresolved = unresolved;
            return Task.CompletedTask;
        }
        public void ClearActivation() => ClearCalls++;
        public void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message) { }
        private void Fail(string phase) { if (FailurePhase == phase) throw new IOException(phase); }
    }

    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Endpoint(TransmitChannelDescriptor channel) : IRadioTrafficEndpoint
    {
        public string Name => "Training";
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => [channel];
        public IReadOnlyCollection<ChannelId> ChannelIds => [channel.Id];
        public bool IsConnected => true;
        public uint? SourceId => 1001;
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot)
            => throw new NotSupportedException();
        public uint CreateStreamId() => 42;
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId)
            => throw new NotSupportedException();
    }
}
