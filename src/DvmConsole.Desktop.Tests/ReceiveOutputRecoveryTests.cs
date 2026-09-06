// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Operations;
using Xunit;
namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveOutputRecoveryTests
{
    private static ChannelViewModel Channel(string name, string id) => new(new ChannelConfiguration
    { Name = name, System = "Audit", Tgid = id, Mode = "dmr", Slot = 1 });
    [Fact]
    public async Task RestartFailurePreservesExistingRxIntent()
    {
        var channel = Channel("Dispatch", "100"); channel.SetAudioEnabled(true);
        var ports = new Ports(channel) { FailStart = true };
        await using var controller = new ReceiveOutputController(ports, ports, ports, ports);
        await controller.StartAsync(channel, persistSelection: false);
        Assert.True(channel.IsAudioEnabled);
    }
    [Fact]
    public async Task DeviceRecoveryDoesNotBlockWorkerDrainWhileReconfigurationOwnsGate()
    {
        var channel = Channel("Dispatch", "100");
        var ports = new Ports(channel);
        await using var controller = new ReceiveOutputController(ports, ports, ports, ports);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool terminated = false;
        await using var work = new ChannelReceiveWorkQueue(async (_, frame) =>
        {
            if (frame.PacketSequence == ushort.MaxValue) { terminated = true; return; }
            entered.SetResult(); await fail.Task;
            controller.RequestRecovery(channel, new IOException("device failed"));
        }, shutdownDrainTimeout: TimeSpan.FromMilliseconds(750),
           cancellationAcknowledgementTimeout: TimeSpan.FromMilliseconds(750));
        FneTrafficFrame Frame(bool end) => new(FneTrafficProtocol.Dmr, 1, 2, 100, 1, "GROUP",
            end ? "TERMINATOR" : "VOICE", end ? "TERMINATOR_WITH_LC" : "VOICE",
            end ? ushort.MaxValue : (ushort)1, 99, []);
        work.Enqueue(channel.Id, Frame(false)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ports.Gate.WaitAsync();
        try
        {
            work.Enqueue(channel.Id, Frame(true));
            var stop = work.StopAsync(channel.Id); fail.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(terminated);
            Assert.True(controller.IsRecoveryRunning(channel));
        }
        finally { ports.Gate.Release(); }
    }
    [Fact]
    public async Task DelayedFailureCannotRestartAReplacementSession()
    {
        var channel = Channel("Dispatch", "100");
        var ports = new Ports(channel);
        await using var controller = new ReceiveOutputController(ports, ports, ports, ports);
        object failedSession = ports.SessionIdentity;
        await ports.Gate.WaitAsync();
        Task<ReceiveRouteRecoveryResult> recovery = controller.RecoverSelectedAsync(channel, expectedSession: failedSession);
        ports.SessionIdentity = new object();
        ports.Gate.Release();
        await recovery;
        Assert.Equal(0, ports.Recoveries);
    }

    [Fact]
    public async Task PartialMuteFailureRestoresSuccessfulChannels()
    {
        var a = Channel("A", "100"); var b = Channel("B", "101");
        a.SetAudioEnabled(true); b.SetAudioEnabled(true);
        var ports = new Ports(a, b) { FailMute = b.Id };
        var controller = new TransmitAudioTransitionController(ports, ports, ports, ports, ports);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.MuteReceiveAudioAsync("sending"));
        Assert.False(a.IsAudioSuspended); Assert.Contains(a.Id, ports.LivePlaybackChannels);
        await controller.RestoreSuspendedAudioAsync();
    }
    [Fact]
    public async Task FailedRestorationRetainsSuspensionAndSelectionForRetry()
    {
        var channel = Channel("Dispatch", "100");
        channel.SetAudioEnabled(true);
        var ports = new Ports(channel) { FailStart = true };
        var controller = new TransmitAudioTransitionController(ports, ports, ports, ports, ports);
        await controller.MuteReceiveAudioAsync("sending");
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RestoreSuspendedAudioAsync());
        Assert.True(channel.IsAudioEnabled);
        Assert.True(channel.IsAudioSuspended);
    }

    [Fact]
    public async Task RestorationDoesNotReopenAnInactiveDeselectedChannel()
    {
        var channel = Channel("Dispatch", "100");
        channel.SetAudioEnabled(true);
        var ports = new Ports(channel) { FailStart = true };
        var controller = new TransmitAudioTransitionController(ports, ports, ports, ports, ports);
        await controller.MuteReceiveAudioAsync("sending");
        channel.SetAudioEnabled(false);
        await controller.RestoreSuspendedAudioAsync();
        Assert.False(channel.IsAudioEnabled);
        Assert.False(channel.IsAudioSuspended);
        Assert.Equal(0, ports.Starts);
    }

    [Fact]
    public void DeletingFilteredPresetKeepsTheFilterAvailable()
    {
        var source = new System.Collections.ObjectModel.ObservableCollection<string>(
            new[] { "Match", "B", "C", "D", "E", "F" });
        var filter = new FilteredPresetCollection<string>(source, value => value);
        filter.FilterText = "Match";
        Assert.True(filter.IsFilterVisible);
        source.Remove("Match");
        Assert.Equal(5, source.Count);
        Assert.True(filter.IsFilterVisible);
        Assert.Empty(filter.Items);
        Assert.Equal("Match", filter.FilterText);
        source.Insert(0, "Match"); // Undo remains visible under the existing filter.
        Assert.Single(filter.Items);
        filter.FilterText = string.Empty;
        Assert.Equal(6, filter.Items.Count);
    }
    private sealed class Ports(params ChannelViewModel[] channels) : IReceiveOutputRoutePort,
        IReceiveOutputMutePort, IReceiveOutputPresentationPort, IReceiveOutputLifetimePort,
        ITransmitReceiveRoutePort, ITransmitReceiveMutePort, ITransmitPermitTonePort,
        ITransmitAudioPresentationPort, ITransmitAudioGate
    {
        private readonly HashSet<ChannelId> live = channels.Select(c => c.Id).ToHashSet();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool FailStart { get; init; }
        public ChannelId? FailMute { get; init; }
        public bool IsDisposing => false;
        public IReadOnlyList<ChannelId> LivePlaybackChannels => live.ToArray();
        public object SessionIdentity { get; set; } = new();
        public int Recoveries { get; private set; }
        public object? GetSessionIdentity(ChannelId id) => SessionIdentity;
        public bool IsActive(ChannelId id) => !FailStart;
        public Task StartAsync(ReceiveChannelDescriptor channel, CancellationToken ct) => FailStart
            ? Task.FromException(new IOException("output device absent")) : Task.CompletedTask;
        public int Starts { get; private set; }
        public Task StartAsync(ChannelViewModel channel) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(ChannelId id, CancellationToken ct) => Task.CompletedTask;
        public Task SetLivePlaybackEnabledAsync(ChannelId id, bool enabled, CancellationToken ct)
        {
            if (!enabled && id == FailMute) throw new IOException("mute device failure");
            if (enabled) live.Add(id); else live.Remove(id);
            return Task.CompletedTask;
        }
        public Task SetLivePlaybackEnabledAsync(ChannelId id, bool enabled) => SetLivePlaybackEnabledAsync(id, enabled, default);
        public void StartWork(ChannelId id) { }
        public Task StopWorkAsync(ChannelId id) => Task.CompletedTask;
        public void ResetDiagnostics(ChannelId id) { }
        public Task<ReceiveRouteRecoveryResult> RecoverSelectedAsync(IReadOnlyCollection<ChannelId> ids, CancellationToken ct)
        { Recoveries++; return Task.FromResult(new ReceiveRouteRecoveryResult([], [], null)); }
        public async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken ct)
        { await Gate.WaitAsync(ct); try { return await op(ct); } finally { Gate.Release(); } }
        public void RecordRestarted(ChannelId id) { }
        public void RecordFailure(ChannelId id, DateTimeOffset at) { }
        public Task ReconcileAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ApplyPlaybackPolicyAsync(IReadOnlyList<(ChannelId ChannelId, bool Enabled)> changes, CancellationToken ct) => Task.CompletedTask;
        public bool IsMuted(ChannelViewModel c) => false;
        public bool ShouldEnableLivePlayback(ChannelViewModel c, bool isTemporarilySuspended) => c.IsAudioEnabled && !isTemporarilySuspended;
        public string? GetEffectiveReason(ChannelViewModel c, bool outputMuted) => null;
        public bool Toggle(SystemViewModel s) => false;
        public bool Toggle(ZoneViewModel z) => false;
        public ChannelViewModel Resolve(ChannelId id) => channels.Single(c => c.Id == id);
        public ChannelViewModel[] Resolve(IEnumerable<ChannelId> ids) => ids.Select(Resolve).ToArray();
        public Task RunAsync(Action action) { action(); return Task.CompletedTask; }
        public Task RunAsync(Func<Task> operation) => operation();
        public void SetSelectionPreference(ChannelViewModel c, bool enabled) { }
        public void StopRecording(ChannelViewModel c) { }
        public void NotifyMuteChanged() { }
        public void PublishStatus(string text) { }
        public void ObserveRecovery(TimeSpan elapsed, string result) { }
        public long SetLivePlaybackDiscarded(bool discarded) => 0;
        public Task SetGainAsync(ChannelId id, double gain) => Task.CompletedTask;
        public double GetVolume(ChannelViewModel c) => 1;
        public void Log(DateTimeOffset at, string source, DebugLogSeverity severity, string text) { }
        public Task<LocalTonePlaybackResult> PlayAsync(LocalTonePlaybackRequest request) => throw new NotSupportedException();
        public Task<LocalTonePlaybackResult> PlayTalkPermitAsync(bool microphoneStartedCold, bool? microphoneIsBluetooth) => throw new NotSupportedException();
    }
}
