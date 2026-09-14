// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ReceiveOutputControllerTests
{
    [Fact]
    public async Task RepeatedReceiveEnableIsIdleUnlessTheSelectedRouteNeedsRecovery()
    {
        var host = new Host();
        await using var controller = host.CreateController();
        await controller.SetEnabledAsync(host.Id, true);
        Assert.Empty(host.Operations);
        host.Active = false;
        await controller.SetEnabledAsync(host.Id, true);
        Assert.Equal(["start", "start-work", "reset"], host.Operations);
        Assert.True(host.State.AudioEnabled);
        Assert.Equal(2, host.ExclusiveCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredOrCancelledSelectionPreparationCannotStartAnOutput(bool retired)
    {
        var host = new Host { Active = false };
        await using var controller = host.CreateController();
        using var cancellation = new CancellationTokenSource();
        Task selection = controller.SetSelectionAsync([host.Id], true,
            (_, _, _) =>
            {
                Assert.True(host.Exclusive);
                if (retired) host.IsDisposing = true;
                else cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellationToken: cancellation.Token);
        if (retired) await Assert.ThrowsAsync<ObjectDisposedException>(() => selection);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.Empty(host.Operations);
        Assert.False(host.Exclusive);
    }

    [Fact]
    public async Task DeselectingRecordedChannelRetainsDecodeAndRecording()
    {
        var host = new Host(recording: true);
        await using var controller = host.CreateController();

        await controller.StopAsync(host.Id, persistSelection: true);

        Assert.Equal(["playback:False"], host.Operations);
        Assert.True(host.Active);
        Assert.False(host.State.AudioEnabled);
        Assert.False(host.Selected);
        Assert.True(host.State.RecordingEnabled);
    }

    [Fact]
    public async Task DeselectingUnrecordedChannelSilencesBeforeRetiringWorkAndRoute()
    {
        var host = new Host();
        await using var controller = host.CreateController();

        await controller.StopAsync(host.Id, persistSelection: true);

        Assert.Equal(["playback:False", "stop-work", "reset", "stop", "stop-recording"], host.Operations);
        Assert.False(host.Active);
        Assert.False(host.State.AudioEnabled);
        Assert.False(host.Selected);
    }

    [Fact]
    public async Task FailureAfterWorkStartsRollsBackServicesAndRetainsOperatorIntent()
    {
        var host = new Host { FailDiagnostics = true, Active = false };
        await using var controller = host.CreateController();

        await controller.StartAsync(host.Id, persistSelection: true);

        Assert.Equal(["start", "start-work", "reset", "stop-work", "stop", "failed"], host.Operations);
        Assert.False(host.Active);
        Assert.True(host.State.AudioEnabled);
        Assert.True(host.Selected);
        Assert.Contains("diagnostics failed", host.Status);
    }

    [Fact]
    public async Task FailedRollbackKeepsSelectionTruthful()
    {
        var host = new Host { FailDiagnostics = true, FailStop = true, Active = false };
        await using var controller = host.CreateController();

        await controller.StartAsync(host.Id, persistSelection: true);

        Assert.True(host.Active);
        Assert.True(host.State.AudioEnabled);
        Assert.True(host.Selected);
        Assert.Contains("Cleanup also failed", host.Status);
    }

    [Fact]
    public async Task RecoveryReappliesMuteBeforeStartingWorkAndSchedulesFailedRoutes()
    {
        var host = new Host { Muted = true };
        await using var controller = host.CreateController();

        await controller.RecoverSelectedAsync(host.Id);

        Assert.Equal(["recover", "playback:False", "restarted", "start-work", "reset", "failed"], host.Operations);
        Assert.Equal(host.UtcNow.AddSeconds(5), host.RetryAt);
    }

    [Fact]
    public async Task MutePolicyDeduplicatesIdsAndRetainsTransmitSuspension()
    {
        var host = new Host();
        host.State = host.State with { AudioSuspended = true };
        await using var controller = host.CreateController();

        await controller.ApplySystemMuteAsync("System", [host.Id, host.Id], muted: false);

        Assert.Equal([(host.Id, false)], host.PlaybackChanges);
        Assert.Equal(["policy", "reconcile", "notify"], host.Operations);
        Assert.Equal("console transmit mute", controller.GetEffectiveMuteReason(host.Id, false));
    }

    private sealed class Host : IReceiveOutputRoutePort, IReceiveOutputMutePort,
        IReceiveOutputPresentationPort, IReceiveOutputLifetimePort
    {
        public Host(bool recording = false)
        {
            State = new(new(Id, new ChannelRuntimeDefinition("Dispatch", "System", "dmr", 100, 0)),
                AudioEnabled: true, RecordingEnabled: recording, AudioSuspended: false);
        }

        public ChannelId Id { get; } = new(new ChannelSessionId("System", ChannelProtocol.Dmr, 100, 0, "Dispatch"));
        public ReceiveOutputChannelState State { get; set; }
        public bool Active { get; set; } = true;
        public bool Selected { get; private set; } = true;
        public bool Muted { get; set; }
        public bool FailDiagnostics { get; set; }
        public bool FailStop { get; set; }
        public List<string> Operations { get; } = [];
        public string Status { get; private set; } = "";
        public DateTimeOffset? RetryAt { get; private set; }
        public IReadOnlyList<(ChannelId ChannelId, bool Enabled)> PlaybackChanges { get; private set; } = [];
        public IReadOnlyList<ChannelId> LivePlaybackChannels => Active ? [Id] : [];
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public bool IsDisposing { get; set; }
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long started) => TimeSpan.Zero;
        public void ObserveRecovery(TimeSpan elapsed, string result) { }
        public ReceiveOutputController CreateController() => new(this, this, this, this);
        public object? GetSessionIdentity(ChannelId channel) => this;
        public bool IsActive(ChannelId channelId) => Active;
        public bool IsMuted(ChannelId channelId) => Muted;
        public bool ShouldEnableLivePlayback(ChannelId channelId, bool isTemporarilySuspended)
            => !Muted && !isTemporarilySuspended;
        public string? GetEffectiveReason(ChannelId channelId, bool outputMuted) => Muted ? "scope mute" : null;
        public ReceiveOutputChannelState Capture(ChannelId channelId) => State;
        public void SetAudioEnabled(ChannelId channelId, bool enabled) => State = State with { AudioEnabled = enabled };
        public void SetSelectionPreference(ChannelId channelId, bool enabled) => Selected = enabled;
        public void PublishStatus(string text) => Status = text;
        public Task RunAsync(Action action) { action(); return Task.CompletedTask; }
        public void StopRecording(ChannelId channelId) => Operations.Add("stop-recording");
        public void NotifyMuteChanged() => Operations.Add("notify");
        public Task StartAsync(ReceiveChannelDescriptor channel, CancellationToken cancellationToken)
        {
            Operations.Add("start");
            Active = true;
            return Task.CompletedTask;
        }
        public Task StopAsync(ChannelId channelId, CancellationToken cancellationToken)
        {
            Operations.Add("stop");
            if (FailStop) throw new IOException("stop failed");
            Active = false;
            return Task.CompletedTask;
        }
        public Task SetLivePlaybackEnabledAsync(ChannelId channelId, bool enabled, CancellationToken cancellationToken)
        {
            Operations.Add($"playback:{enabled}");
            return Task.CompletedTask;
        }
        public void StartWork(ChannelId channelId) => Operations.Add("start-work");
        public Task StopWorkAsync(ChannelId channelId) { Operations.Add("stop-work"); return Task.CompletedTask; }
        public void ResetDiagnostics(ChannelId channelId)
        {
            Operations.Add("reset");
            if (FailDiagnostics) throw new IOException("diagnostics failed");
        }
        public Task<ReceiveRouteRecoveryResult> RecoverSelectedAsync(IReadOnlyCollection<ChannelId> channelIds,
            CancellationToken cancellationToken)
        {
            Operations.Add("recover");
            ChannelId failed = new(new ChannelSessionId("System", ChannelProtocol.Dmr, 101, 0, "Other"));
            return Task.FromResult(new ReceiveRouteRecoveryResult([Id], [failed], "route unavailable"));
        }
        public bool Exclusive { get; private set; }
        public int ExclusiveCalls { get; private set; }
        public async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            Assert.False(Exclusive);
            Exclusive = true;
            ExclusiveCalls++;
            try { return await operation(cancellationToken); }
            finally { Exclusive = false; }
        }
        public void RecordRestarted(ChannelId channelId) => Operations.Add("restarted");
        public void RecordFailure(ChannelId channelId, DateTimeOffset retryAt) { Operations.Add("failed"); RetryAt = retryAt; }
        public Task ReconcileAsync(CancellationToken cancellationToken) { Operations.Add("reconcile"); return Task.CompletedTask; }
        public Task ApplyPlaybackPolicyAsync(IReadOnlyList<(ChannelId ChannelId, bool Enabled)> changes,
            CancellationToken cancellationToken)
        {
            Operations.Add("policy");
            PlaybackChanges = changes;
            return Task.CompletedTask;
        }
    }
}
