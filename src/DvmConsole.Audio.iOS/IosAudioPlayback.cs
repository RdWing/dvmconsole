// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;

namespace DvmConsole.Audio;

internal sealed class IosAudioPlayback : IAudioPlayback, IImmediateAudioStop, IAudioPlaybackCallbackDiagnostics, IAudioPlaybackContinuityDiagnostics
{
    private readonly IosAudioSessionOwner owner;
    private readonly Action<IAsyncDisposable> retired;
    private readonly object queueSync = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposeSync = new();
    private Task? disposal;
    // Writes are serialized; reuse conversion scratch buffers across audio blocks.
    private readonly short[] stereo = new short[320];
    private short[] converted = [];
    private PcmRateConverter? converter;
    private IosAudioDeviceVersion converterVersion;
    private int disposed;
    private int pendingHardwareSamples;
    public IosAudioPlayback(IosAudioSessionOwner owner, PcmAudioFormat format, Action<IAsyncDisposable> retired)
    {
        this.owner = owner;
        this.retired = retired;
        Format = format;
        owner.AcquireOutput();
    }
    public PcmAudioFormat Format { get; }
    public long OutputCallbackCount => Volatile.Read(ref disposed) != 0 ? 0 : owner.CallbackCount;
    public TimeSpan StarvedDuration => Volatile.Read(ref disposed) != 0
        ? TimeSpan.Zero : owner.CaptureDiagnostics().StarvedDuration;
    public TimeSpan PendingStarvedDuration => Volatile.Read(ref disposed) != 0
        ? TimeSpan.Zero : owner.CaptureDiagnostics().PendingStarvedDuration;
    public void EndExpectedPlayback()
    {
        lock (disposeSync)
        {
            if (Volatile.Read(ref disposed) == 0) owner.EndExpectedPlayback();
        }
    }
    public int? QueuedSamples
    {
        get
        {
            if (Volatile.Read(ref disposed) != 0) return 0;
            try
            {
                lock (queueSync)
                {
                    if (!owner.TryGetPlaybackVersion(out IosAudioDeviceVersion version)) return 0;
                    return checked((int)Math.Ceiling((owner.QueuedOutput + pendingHardwareSamples) *
                        (double)Format.SampleRate * Format.Channels / (version.SampleRate * 2)));
                }
            }
            catch (InvalidOperationException) { return 0; }
        }
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (samples.Length % Format.Channels != 0) throw new ArgumentException("PCM must contain complete frames.", nameof(samples));
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.TryGetPlaybackVersion(out IosAudioDeviceVersion version)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await writes.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (converter is null || converterVersion != version)
            {
                converter = new PcmRateConverter(Format.SampleRate, version.SampleRate, channels: 2);
                converterVersion = version;
            }
            for (int offset = 0; offset < samples.Length;)
            {
                linked.Token.ThrowIfCancellationRequested();
                int frames = Math.Min(160, (samples.Length - offset) / Format.Channels);
                for (int frame = 0; frame < frames; frame++)
                {
                    stereo[frame * 2] = samples.Span[offset + frame * Format.Channels];
                    stereo[frame * 2 + 1] = samples.Span[offset + frame * Format.Channels + Format.Channels - 1];
                }
                int required = converter.GetMaximumOutputSampleCount(frames * 2);
                if (converted.Length < required) converted = new short[required];
                int convertedCount = converter.Convert(stereo.AsSpan(0, frames * 2), converted);
                int written = 0;
                long progress = Stopwatch.GetTimestamp();
                while (written < convertedCount)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    int accepted;
                    lock (queueSync)
                    {
                        pendingHardwareSamples = convertedCount - written;
                        accepted = owner.Write(version, converted.AsSpan(written, convertedCount - written));
                        written += accepted;
                        pendingHardwareSamples = convertedCount - written;
                    }
                    if (accepted > 0) progress = Stopwatch.GetTimestamp();
                    else
                    {
                        if (Stopwatch.GetElapsedTime(progress) > TimeSpan.FromSeconds(2))
                            throw new TimeoutException("The iOS output stopped consuming audio.");
                        await Task.Delay(5, linked.Token).ConfigureAwait(false);
                    }
                }
                offset += frames * Format.Channels;
            }
        }
        catch { converter = null; throw; }
        finally { lock (queueSync) pendingHardwareSamples = 0; writes.Release(); }
    }
    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
    public async ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await writes.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            int initial = QueuedSamples ?? 0;
            long started = Stopwatch.GetTimestamp();
            while ((QueuedSamples ?? 0) > 0)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("The iOS output did not drain.");
                await Task.Delay(5, linked.Token).ConfigureAwait(false);
            }
            return initial;
        }
        finally { writes.Release(); }
    }
    public void StopImmediately()
    {
        lock (disposeSync)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            lifetime.Cancel();
            owner.StopImmediately();
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (disposeSync) return new ValueTask(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        lifetime.Cancel();
        await writes.WaitAsync().ConfigureAwait(false);
        try { owner.ReleaseOutput(); }
        finally { writes.Release(); retired(this); }
        // Keep the small synchronization objects valid for callers racing
        // disposal; their operations observe cancellation or the disposed flag.
    }
}
