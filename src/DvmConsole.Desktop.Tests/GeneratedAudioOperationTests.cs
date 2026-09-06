// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only
using DvmConsole.Application;
using DvmConsole.Audio;
using Xunit;
namespace DvmConsole.Desktop.Tests;

public sealed class GeneratedAudioOperationTests
{
    [Fact]
    public async Task CompleteLifecycleIsSerializedThroughRestoration()
    {
        var port = new Port();
        await using var owner = new GeneratedAudioOperation(port);
        Task<Exception?> first = owner.SendAsync([], new short[] { 1 });
        await port.Started.WaitAsync();
        Task<Exception?> second = owner.SendAsync([], new short[] { 2 });
        Assert.Equal(1, port.Admissions);
        port.Continue.Release();
        await first;
        await port.Started.WaitAsync();
        Assert.True(port.Muted);
        Assert.Equal(1, port.Restorations);
        port.Continue.Release();
        await second;
        Assert.False(port.Muted);
        Assert.Equal(2, port.Restorations);
    }

    [Fact]
    public async Task FailedMuteIsReleasedWithoutStartingMonitorOrTransmission()
    {
        var port = new Port { FailMute = true };
        await using var owner = new GeneratedAudioOperation(port);
        await Assert.ThrowsAsync<IOException>(() => owner.SendAsync([], new short[] { 1 }));
        Assert.False(port.Muted);
        Assert.Equal(1, port.Restorations);
        Assert.Equal(0, port.Transmissions);
    }

    [Fact]
    public async Task DisposalCancelsActiveAndQueuedSendsAndJoinsRestoration()
    {
        var port = new Port();
        var owner = new GeneratedAudioOperation(port);
        Task<Exception?> active = owner.SendAsync([], new short[] { 1 });
        await port.Started.WaitAsync();
        Task<Exception?> queued = owner.SendAsync([], new short[] { 2 });
        await owner.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(port.Muted);
        Assert.Equal(1, port.Restorations);
        Assert.Equal(1, port.Admissions);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.SendAsync([], new short[] { 3 }));
    }

    [Fact]
    public async Task SessionDrainCancelsOldWorkAndAllowsTheSessionToResume()
    {
        var port = new Port();
        await using var owner = new GeneratedAudioOperation(port);
        Task<Exception?> old = owner.SendAsync([], new short[] { 1 });
        await port.Started.WaitAsync();
        await owner.CancelAndDrainAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Task<Exception?> resumed = owner.SendAsync([], new short[] { 2 });
        await port.Started.WaitAsync();
        port.Continue.Release();
        await resumed;
        Assert.Equal(2, port.Restorations);
    }

    [Fact]
    public async Task DisposalCancelsMonitoringAfterTransmissionHasFinished()
    {
        var port = new Port { HoldMonitor = true };
        var owner = new GeneratedAudioOperation(port);
        var sequence = new GeneratedToneSequence([
            GeneratedToneStep.Tone(1000, TimeSpan.FromMilliseconds(20))]);
        Task<Exception?> send = owner.SendAsync([], sequence);
        await port.Started.WaitAsync();
        port.Continue.Release();
        await port.Transmitted.Task;
        Task<Exception?> queued = owner.SendAsync([], new short[] { 2 });
        Assert.Equal(1, port.Admissions);
        Assert.True(port.Muted);
        await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(port.Muted);
        Assert.Equal(1, port.Restorations);
        await owner.CancelAndDrainAsync();
    }

    private sealed class Port : IGeneratedAudioOperationPort
    {
        public SemaphoreSlim Started { get; } = new(0);
        public SemaphoreSlim Continue { get; } = new(0);
        public int Admissions, Restorations, Transmissions;
        public bool Muted, FailMute, HoldMonitor;
        public TaskCompletionSource Transmitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool MonitorEnabled => true;
        public ValueTask<IAsyncDisposable> EnterTransmitAsync(CancellationToken token)
        { Admissions++; return ValueTask.FromResult<IAsyncDisposable>(new Admission()); }
        public void Validate(IReadOnlyList<TransmitTarget> targets) { }
        public Task MuteReceiveAsync()
        { Muted = true; return FailMute ? Task.FromException(new IOException("mute failed")) : Task.CompletedTask; }
        public Task RestoreReceiveAsync() { Muted = false; Restorations++; return Task.CompletedTask; }
        public async Task MonitorAsync(ReadOnlyMemory<short> samples, CancellationToken token)
        {
            Assert.True(Muted);
            if (HoldMonitor)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        public async Task TransmitAsync(IReadOnlyList<TransmitTarget> targets, ReadOnlyMemory<short> samples,
            GeneratedToneSequence? sequence, CancellationToken token)
        { Assert.True(Muted); Transmissions++; Started.Release(); await Continue.WaitAsync(token); Transmitted.TrySetResult(); }
        private sealed class Admission : IAsyncDisposable
        { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
