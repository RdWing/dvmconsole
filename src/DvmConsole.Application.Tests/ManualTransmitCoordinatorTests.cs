// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ManualTransmitCoordinatorTests
{
    [Fact]
    public async Task ChannelReleaseCancelsOnlyItsOwnPendingStartup()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var context = new Context();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pending = default;
        var coordinator = Create(gate, context, async request =>
        {
            pending = request.CancellationToken;
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, pending);
        });
        Task start = coordinator.StartAsync([context.Channel.Id]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = new ConsoleChannelState(new("Other", "System", "p25", 101, 0));
        Assert.False(coordinator.CancelChannelStartup(other.Id));
        Assert.False(pending.IsCancellationRequested);
        Assert.True(coordinator.CancelChannelStartup(context.Channel.Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, gate.CurrentCount);
        Assert.False(coordinator.CancelChannelStartup(context.Channel.Id));
    }

    [Fact]
    public async Task BusyAdmissionDropsThePressWithoutCapturingOrQueuingTargets()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var context = new Context();
        int starts = 0;
        var coordinator = Create(gate, context, _ => { starts++; return Task.CompletedTask; });
        await coordinator.StartAsync([context.Channel.Id]).WaitAsync(TimeSpan.FromSeconds(30));
        gate.Release();
        Assert.Equal(0, starts);
        Assert.Equal(0, context.Captures);
        Assert.Contains("another transmit operation", context.Status);
    }

    [Theory]
    [InlineData("suppressed")]
    [InlineData("demo")]
    [InlineData("active")]
    public async Task UnavailableSessionDoesNotCaptureTargets(string condition)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var context = new Context
        {
            IsInputSuppressed = condition == "suppressed",
            NetworkDisabled = condition == "demo",
            HasActiveTransmission = condition == "active"
        };
        var coordinator = Create(gate, context, _ => throw new InvalidOperationException("Must not start"));
        await coordinator.StartAsync([context.Channel.Id]);
        Assert.Equal(0, context.Captures);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveAdmissionHonorsCurrentCallPriority(bool priority)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var context = new Context();
        context.Channel = context.Channel with { ReceiveActive = true, AllowsTransmitDuringReceive = priority };
        TransmitStartRequest? request = null;
        var coordinator = Create(gate, context, value => { request = value; return Task.CompletedTask; });
        await coordinator.StartAsync([context.Channel.Id]);
        if (priority)
        {
            Assert.Equal(context.Channel, Assert.Single(request!.Targets).Channel);
            Assert.True(request.PlayPermitTone);
            Assert.True(context.Starting);
        }
        else
        {
            Assert.Null(request);
            Assert.False(context.Starting);
            Assert.Contains("currently receiving", context.Status);
        }
    }

    [Fact]
    public async Task MissingSystemDoesNotPresentStarting()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var context = new Context { MissingSystem = true };
        var coordinator = Create(gate, context, _ => throw new InvalidOperationException("Must not start"));
        await coordinator.StartAsync([context.Channel.Id]);
        Assert.False(context.Starting);
        Assert.Contains("was not found", context.Status);
    }

    [Fact]
    public async Task FailedStartupReleasesAdmissionForTheNextAttempt()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var context = new Context();
        int attempts = 0;
        var coordinator = Create(gate, context, _ =>
            ++attempts == 1 ? Task.FromException(new IOException("Startup failed")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync([context.Channel.Id]));
        await coordinator.StartAsync([context.Channel.Id]);
        Assert.Equal(2, attempts);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task ReleaseWaitsForAdmittedStartupAndPreservesStopOptions()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var context = new Context();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stopped = false;
        var coordinator = new ManualTransmitCoordinator(gate, context, async _ =>
        {
            entered.SetResult();
            await release.Task;
        }, (ids, status, propagate) =>
        {
            Assert.Equal(context.Channel.Id, Assert.Single(ids));
            Assert.Equal("Released", status);
            Assert.True(propagate);
            stopped = true;
            return Task.CompletedTask;
        });
        Task startup = coordinator.StartAsync([context.Channel.Id]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Task stop = coordinator.StopAsync([context.Channel.Id], "Released", true);
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.False(stopped);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(startup, stop).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(stopped);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task ReleaseRevokesStartupBeforeWaitingAndANewPressGetsFreshIntent()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var context = new Context();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken first = default;
        int attempts = 0;
        var coordinator = Create(gate, context, async request =>
        {
            if (++attempts == 1)
            {
                first = request.CancellationToken;
                entered.SetResult();
                await finish.Task;
            }
            else Assert.False(request.CancellationToken.IsCancellationRequested);
        });
        Task press = coordinator.StartAsync([context.Channel.Id]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Task release = coordinator.StopAsync([context.Channel.Id]);
        try
        {
            Assert.True(first.IsCancellationRequested);
            Assert.False(release.IsCompleted);
        }
        finally { finish.TrySetResult(); }
        await Task.WhenAll(press, release).WaitAsync(TimeSpan.FromSeconds(30));
        await coordinator.StartAsync([context.Channel.Id]);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SessionFenceRevokesStartupWithoutWaitingForAdmissionOrStartingCleanup()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var context = new Context();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken intent = default;
        int stops = 0;
        var coordinator = new ManualTransmitCoordinator(gate, context, async request =>
        {
            intent = request.CancellationToken;
            entered.SetResult();
            await finish.Task;
        }, (_, _, _) => { stops++; return Task.CompletedTask; });
        Task press = coordinator.StartAsync([context.Channel.Id]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            coordinator.CancelStartup();
            coordinator.CancelStartup();
            Assert.True(intent.IsCancellationRequested);
            Assert.False(press.IsCompleted);
            Assert.Equal(0, stops);
        }
        finally { finish.TrySetResult(); }
        await press.WaitAsync(TimeSpan.FromSeconds(30));
        await coordinator.StopAsync([context.Channel.Id]);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task FailedReleaseDoesNotRetainAdmission()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var context = new Context();
        int starts = 0;
        var coordinator = new ManualTransmitCoordinator(gate, context,
            _ => { starts++; return Task.CompletedTask; },
            (_, _, _) => Task.FromException(new IOException("Unconfirmed release")));
        await Assert.ThrowsAsync<IOException>(() => coordinator.StopAsync([context.Channel.Id]));
        await coordinator.StartAsync([context.Channel.Id]);
        Assert.Equal(1, starts);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task CallerCancellationRevokesStartupAndDoesNotPoisonTheNextPress()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var context = new Context();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        var coordinator = Create(gate, context, async request =>
        {
            if (++attempts != 1)
            {
                Assert.False(request.CancellationToken.IsCancellationRequested);
                return;
            }
            entered.SetResult();
            await release.Task.WaitAsync(request.CancellationToken);
        });
        Task press = coordinator.StartAsync([context.Channel.Id], cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => press.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(1, gate.CurrentCount);
            await coordinator.StartAsync([context.Channel.Id]);
            Assert.Equal(2, attempts);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.StartAsync([context.Channel.Id], cancellation.Token));
            Assert.Equal(2, attempts);
        }
        finally { release.TrySetResult(); }
    }

    private static ManualTransmitCoordinator Create(SemaphoreSlim gate, Context context,
        Func<TransmitStartRequest, Task> start)
        => new(gate, context, start, (_, _, _) => Task.CompletedTask);

    private sealed class Context : IManualTransmitStartContext
    {
        public bool IsInputSuppressed { get; set; }
        public bool NetworkDisabled { get; set; }
        public bool HasActiveTransmission { get; set; }
        public bool PlayPermitTone => true;
        public bool MissingSystem { get; set; }
        public bool Starting { get; private set; }
        public int Captures { get; private set; }
        public string Status { get; private set; } = "";
        public TransmitChannelDescriptor Channel { get; set; } = CreateChannel();
        private static TransmitChannelDescriptor CreateChannel()
        {
            var state = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "System", "p25", 100, 0));
            return state.CaptureTransmitDescriptor(new ChannelConfigurationAccess(state.Runtime.Definition));
        }
        public TransmitChannelDescriptor CaptureChannel(ChannelId id) { Captures++; Assert.Equal(Channel.Id, id); return Channel; }
        public IRadioTrafficEndpoint? ResolveSystem(string name) => MissingSystem ? null : new Endpoint();
        public Task SetStatusAsync(string status) { Status = status; return Task.CompletedTask; }
        public Task SetStartingAsync(IReadOnlyList<ChannelId> channels) { Starting = true; return Task.CompletedTask; }
    }

    private sealed class Endpoint : IRadioTrafficEndpoint
    {
        public string Name => "System";
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => [];
        public IReadOnlyCollection<ChannelId> ChannelIds => [];
        public bool IsConnected => true;
        public uint? SourceId => 42;
        public TargetAuthorityState GetTargetAuthority(RadioMediaProtocol protocol, uint destinationId, byte runtimeSlot) => default;
        public uint CreateStreamId() => 77;
        public void SendTraffic(RadioMediaProtocol protocol, ReadOnlyMemory<byte> payload, ushort packetSequence, uint streamId)
            => throw new InvalidOperationException("Admission test must not send packets.");
    }
}
