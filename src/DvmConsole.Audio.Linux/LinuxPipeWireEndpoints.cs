// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace DvmConsole.Audio;

internal sealed class LinuxPipeWireCapture :
    IAudioCapture,
    IBorrowedAudioCapture,
    IImmediateAudioStop
{
    private readonly IPipeWireApi api;
    private readonly SafePipeWireStreamHandle stream;
    private readonly NativeCapturePump pump;
    private bool disposed;

    public LinuxPipeWireCapture(IPipeWireApi api, ulong deviceId, PcmAudioFormat format)
    {
        this.api = api;
        Format = format;
        stream = api.CreateStream(deviceId, input: 1, format.SampleRate, format.Channels, format.BitsPerSample);
        if (stream.IsInvalid)
        {
            stream.Dispose();
            throw new InvalidOperationException("PipeWire could not create the capture stream.");
        }
        pump = new NativeCapturePump(
            1600,
            () =>
            {
                int result = api.WaitForCapture(stream, Timeout.Infinite);
                LinuxPipeWireBackend.EnsureNonNegative(result, "wait for PipeWire capture audio");
                return result;
            },
            buffer =>
            {
                int count = api.ReadStream(stream, buffer, buffer.Length);
                LinuxPipeWireBackend.EnsureNonNegative(count, "read PipeWire capture audio");
                return count;
            },
            () => api.WakeCapture(stream),
            PublishSamples);
    }

    public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
    public event BorrowedPcmSamplesHandler? BorrowedSamplesAvailable;
    public PcmAudioFormat Format { get; }
    public bool IsRunning => pump.IsRunning;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRunning)
            return ValueTask.CompletedTask;

        LinuxPipeWireBackend.EnsureSuccess(api.StartStream(stream), "start PipeWire capture");
        try
        {
            pump.Start();
        }
        catch
        {
            api.StopStream(stream);
            throw;
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Exception? pumpFailure = null;
        try
        {
            await pump.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            pumpFailure = exception;
        }
        finally
        {
            int stopResult = api.StopStream(stream);
            if (pumpFailure is null)
                LinuxPipeWireBackend.EnsureSuccess(stopResult, "stop PipeWire capture");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (pumpFailure is not null)
            ExceptionDispatchInfo.Capture(pumpFailure).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            stream.Dispose();
            disposed = true;
        }
    }

    public void StopImmediately()
    {
        if (disposed)
            return;
        pump.RequestStop();
        api.StopStream(stream);
    }

    private void PublishSamples(short[] buffer, int count)
    {
        ReadOnlySpan<short> samples = buffer.AsSpan(0, count);
        BorrowedSamplesAvailable?.Invoke(samples);
        EventHandler<PcmSamplesEventArgs>? owned = SamplesAvailable;
        if (owned is not null)
            owned(this, new PcmSamplesEventArgs(samples.ToArray()));
    }

}

internal sealed class LinuxPipeWirePlayback :
    IAudioPlayback,
    IAudioPlaybackContinuityDiagnostics,
    IAudioPlaybackCallbackDiagnostics,
    IImmediateAudioStop
{
    private static readonly TimeSpan WriteNoProgressTimeout = TimeSpan.FromSeconds(2);
    private readonly IPipeWireApi api;
    private readonly SafePipeWireStreamHandle stream;
    private readonly int nativeSampleRate;
    private bool disposed;

    public LinuxPipeWirePlayback(IPipeWireApi api, ulong deviceId, PcmAudioFormat format)
    {
        this.api = api;
        Format = format;
        stream = api.CreateStream(deviceId, input: 0, format.SampleRate, format.Channels, format.BitsPerSample);
        if (stream.IsInvalid)
        {
            stream.Dispose();
            throw new InvalidOperationException("PipeWire could not create the playback stream.");
        }

        try
        {
            nativeSampleRate = api.GetSampleRate(stream);
            if (nativeSampleRate <= 0)
                throw new InvalidOperationException("PipeWire returned an invalid playback sample rate.");
            LinuxPipeWireBackend.EnsureSuccess(api.StartStream(stream), "start PipeWire playback");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public PcmAudioFormat Format { get; }
    public int? QueuedSamples => ConvertToRequestedRate(api.GetQueuedSamples(stream));
    public TimeSpan StarvedDuration => TimeSpan.FromSeconds(
        api.GetStarvedSamples(stream) /
        (double)checked(nativeSampleRate * Format.Channels));
    public TimeSpan PendingStarvedDuration => TimeSpan.FromSeconds(
        api.GetPendingStarvedSamples(stream) /
        (double)checked(nativeSampleRate * Format.Channels));
    public long OutputCallbackCount => checked((long)api.GetOutputCallbackCount(stream));

    public void EndExpectedPlayback()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        api.EndPlaybackContinuity(stream);
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (samples.IsEmpty)
            return;

        short[] buffer = ArrayPool<short>.Shared.Rent(samples.Length);
        try
        {
            samples.Span.CopyTo(buffer);
            int remaining = samples.Length;
            long lastProgress = Stopwatch.GetTimestamp();
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int accepted = api.WriteStream(stream, buffer, remaining);
                if (accepted < 0)
                    LinuxPipeWireBackend.EnsureSuccess(accepted, "write PipeWire playback audio");
                if (accepted > 0)
                {
                    remaining -= accepted;
                    if (remaining > 0)
                        Array.Copy(buffer, accepted, buffer, 0, remaining);
                    lastProgress = Stopwatch.GetTimestamp();
                    continue;
                }

                if (Stopwatch.GetElapsedTime(lastProgress) >= WriteNoProgressTimeout)
                    throw new TimeoutException("PipeWire output stopped consuming audio for two seconds.");
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<short>.Shared.Return(buffer);
        }
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int initialSamples = QueuedSamples ?? 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while ((QueuedSamples ?? 0) > 0)
                await Task.Delay(5, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("PipeWire audio playback did not drain within five seconds.");
        }

        return initialSamples - (QueuedSamples ?? 0);
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            api.StopStream(stream);
            stream.Dispose();
            disposed = true;
        }
        return ValueTask.CompletedTask;
    }

    public void StopImmediately()
    {
        if (!disposed)
            api.StopStream(stream);
    }

    private int ConvertToRequestedRate(uint nativeSamples)
    {
        long scaled = checked((long)nativeSamples * Format.SampleRate);
        return checked((int)((scaled + nativeSampleRate - 1) / nativeSampleRate));
    }
}
