// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Audio;

internal sealed class IosAudioCapture :
    IAudioCapture, IBorrowedAudioCapture, IImmediateAudioStop
{
    private readonly IosAudioSessionOwner owner;
    private readonly Action<IAsyncDisposable> retired;
    private Task? disposal;
    private readonly SemaphoreSlim transitions = new(1, 1);
    private readonly object sync = new();
    private CancellationTokenSource? runningCancellation;
    private Task worker = Task.CompletedTask;
    private long generation;
    private bool disposed;
    private int running;
    public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
    public event BorrowedPcmSamplesHandler? BorrowedSamplesAvailable;
    public PcmAudioFormat Format { get; }
    public IosAudioCapture(IosAudioSessionOwner owner, PcmAudioFormat format, Action<IAsyncDisposable> retired)
    {
        this.owner = owner;
        this.retired = retired;
        Format = format;
        owner.Changed += HandleAudioChange;
    }
    private void HandleAudioChange(object? sender, IosAudioSessionChange change)
    {
        if (!change.ManualTransmitMustStop) return;
        lock (sync) { generation++; runningCancellation?.Cancel(); }
    }
    public bool IsRunning => Volatile.Read(ref running) != 0;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        long requestedGeneration;
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); requestedGeneration = generation; }
        await transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource cancellation;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (generation != requestedGeneration) throw new OperationCanceledException("Capture startup was retired.");
                if (IsRunning) return;
                runningCancellation?.Dispose();
                cancellation = runningCancellation = new CancellationTokenSource();
            }
            bool acquired = false;
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancellation.Token);
                await owner.StartCaptureAsync(this, startup.Token).ConfigureAwait(false);
                acquired = true;
                startup.Token.ThrowIfCancellationRequested();
                IosAudioDeviceVersion version = owner.Version;
                Volatile.Write(ref running, 1);
                worker = Task.Factory.StartNew(() => Capture(version, cancellation.Token),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch
            {
                if (acquired) owner.StopCapture(this);
                lock (sync)
                {
                    if (ReferenceEquals(runningCancellation, cancellation)) runningCancellation = null;
                }
                cancellation.Dispose();
                throw;
            }
        }
        finally { transitions.Release(); }
    }
    private void Capture(IosAudioDeviceVersion version, CancellationToken cancellationToken)
    {
        var converter = new PcmRateConverter(version.SampleRate, Format.SampleRate);
        short[] native = new short[4096];
        try
        {
            using var signal = owner.AcquireCaptureSignal(version, cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = owner.Read(version, native);
                if (count == 0) { signal.Wait(); continue; }
                ReadOnlySpan<short> samples = converter.ConvertBorrowed(native.AsSpan(0, count));
                if (samples.Length == 0) continue;
                cancellationToken.ThrowIfCancellationRequested();
                BorrowedSamplesAvailable?.Invoke(samples);
                EventHandler<PcmSamplesEventArgs>? owned = SamplesAvailable;
                if (owned is not null)
                    owned(this, new PcmSamplesEventArgs(samples.ToArray()));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("iOS microphone capture stopped: {0}", exception);
            owner.StopCaptureImmediately(this);
        }
        finally { Volatile.Write(ref running, 0); }
    }
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (sync) { generation++; runningCancellation?.Cancel(); }
        await transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await worker.ConfigureAwait(false);
            owner.StopCapture(this);
            lock (sync) { runningCancellation?.Dispose(); runningCancellation = null; }
        }
        finally { transitions.Release(); }
    }
    public void StopImmediately()
    {
        lock (sync) { generation++; runningCancellation?.Cancel(); }
        owner.StopCaptureImmediately(this);
    }
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposed = true;
            return new ValueTask(disposal ??= DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { owner.Changed -= HandleAudioChange; retired(this); }
    }
}
