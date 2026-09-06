// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

internal sealed class MacCoreAudioCapture :
    IAudioCapture,
    IBorrowedAudioCapture,
    IImmediateAudioStop
{
    private readonly NativeCoreAudioApi api;
    private readonly SafeCoreAudioStreamHandle stream;
    private readonly PcmRateConverter? rateConverter;
    private readonly NativeCapturePump pump;
    private short[] conversionBuffer = [];
    private bool disposed;

    public MacCoreAudioCapture(
        NativeCoreAudioApi api,
        ulong deviceId,
        PcmAudioFormat format)
    {
        MacCoreAudioBackend.ValidateVoiceFormat(format);
        this.api = api;
        Format = format;
        SafeCoreAudioStreamHandle? createdStream = null;
        try
        {
            createdStream = api.CreateStream(
                deviceId,
                input: 1,
                format.SampleRate,
                format.Channels,
                format.BitsPerSample);
            if (createdStream.IsInvalid)
                throw new InvalidOperationException("CoreAudio could not create the capture stream.");
            int nativeSampleRate = api.GetSampleRate(createdStream);
            rateConverter = nativeSampleRate == format.SampleRate
                ? null
                : new PcmRateConverter(nativeSampleRate, format.SampleRate);
            stream = createdStream;
            pump = new NativeCapturePump(
                1600,
                () =>
                {
                    int result = api.WaitForCapture(stream, Timeout.Infinite);
                    MacCoreAudioBackend.EnsureNonNegative(result, "wait for CoreAudio capture audio");
                    return result;
                },
                buffer =>
                {
                    int count = api.ReadStream(stream, buffer, buffer.Length);
                    MacCoreAudioBackend.EnsureNonNegative(count, "read CoreAudio capture audio");
                    return count;
                },
                () => api.WakeCapture(stream),
                PublishSamples);
        }
        catch
        {
            createdStream?.Dispose();
            throw;
        }
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

        MacCoreAudioBackend.EnsureSuccess(api.StartStream(stream), "start CoreAudio capture");
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
        if (!pump.HasStarted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

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
                MacCoreAudioBackend.EnsureSuccess(stopResult, "stop CoreAudio capture");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (pumpFailure is not null)
            ExceptionDispatchInfo.Capture(pumpFailure).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        Exception? stopFailure = null;
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }
        finally
        {
            try
            {
                stream.Dispose();
            }
            finally
            {
                disposed = true;
            }
        }

        if (stopFailure is not null)
            ExceptionDispatchInfo.Capture(stopFailure).Throw();
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
        if (rateConverter is null)
        {
            PublishBorrowed(buffer.AsSpan(0, count));
            return;
        }

        int maximumOutputSamples = rateConverter.GetMaximumOutputSampleCount(count);
        if (maximumOutputSamples == 0)
        {
            rateConverter.Convert(buffer.AsSpan(0, count), Span<short>.Empty);
            return;
        }

        if (conversionBuffer.Length < maximumOutputSamples)
            conversionBuffer = new short[maximumOutputSamples];
        Span<short> converted = conversionBuffer.AsSpan(0, maximumOutputSamples);
        int convertedCount = rateConverter.Convert(buffer.AsSpan(0, count), converted);
        if (convertedCount > 0)
        {
            PublishBorrowed(converted[..convertedCount]);
        }
    }

    private void PublishBorrowed(ReadOnlySpan<short> samples)
    {
        BorrowedSamplesAvailable?.Invoke(samples);
        EventHandler<PcmSamplesEventArgs>? owned = SamplesAvailable;
        if (owned is not null)
            owned(this, new PcmSamplesEventArgs(samples.ToArray()));
    }
}

internal sealed class MacCoreAudioPlayback :
    IAudioPlayback,
    IAudioPlaybackContinuityDiagnostics,
    IAudioPlaybackCallbackDiagnostics,
    IAudioPlaybackPresentationLatencyDiagnostics,
    IPcmWriteTarget,
    IImmediateAudioStop
{
    private static readonly TimeSpan DefaultWriteNoProgressTimeout = TimeSpan.FromSeconds(2);
    private readonly NativeCoreAudioApi api;
    private readonly SafeCoreAudioStreamHandle stream;
    private readonly PcmRateConverter? rateConverter;
    private readonly int nativeSampleRate;
    private readonly TimeSpan writeNoProgressTimeout;
    private bool disposed;

    public MacCoreAudioPlayback(
        NativeCoreAudioApi api,
        ulong deviceId,
        PcmAudioFormat format,
        TimeSpan? writeNoProgressTimeout = null)
    {
        MacCoreAudioBackend.ValidatePlaybackFormat(format);
        this.api = api;
        this.writeNoProgressTimeout = writeNoProgressTimeout ?? DefaultWriteNoProgressTimeout;
        if (this.writeNoProgressTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(writeNoProgressTimeout));
        Format = format;
        SafeCoreAudioStreamHandle? createdStream = null;
        try
        {
            createdStream = api.CreateStream(
                deviceId,
                input: 0,
                format.SampleRate,
                format.Channels,
                format.BitsPerSample);
            if (createdStream.IsInvalid)
                throw new InvalidOperationException("CoreAudio could not create the playback stream.");
            nativeSampleRate = api.GetSampleRate(createdStream);
            rateConverter = nativeSampleRate == format.SampleRate
                ? null
                : new PcmRateConverter(format.SampleRate, nativeSampleRate, format.Channels);
            MacCoreAudioBackend.EnsureSuccess(api.StartStream(createdStream), "start CoreAudio playback");
            stream = createdStream;
        }
        catch
        {
            if (createdStream is not null)
            {
                api.StopStream(createdStream);
                createdStream.Dispose();
            }
            throw;
        }
    }

    public PcmAudioFormat Format { get; }
    public int? QueuedSamples => MacCoreAudioBackend.ConvertQueueDepthToRequestedRate(
        api.GetQueuedSamples(stream),
        nativeSampleRate,
        Format.SampleRate);
    public TimeSpan StarvedDuration => TimeSpan.FromSeconds(
        api.GetStarvedSamples(stream) /
        (double)checked(nativeSampleRate * Format.Channels));
    public TimeSpan PendingStarvedDuration => TimeSpan.FromSeconds(
        api.GetPendingStarvedSamples(stream) /
        (double)checked(nativeSampleRate * Format.Channels));
    public long OutputCallbackCount => checked((long)api.GetOutputCallbackCount(stream));
    public TimeSpan OutputPresentationLatency => api.GetOutputPresentationLatency(stream);

    public void EndExpectedPlayback()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        api.EndPlaybackContinuity(stream);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (rateConverter is null)
        {
            await WriteUnconvertedAsync(samples, cancellationToken).ConfigureAwait(false);
            return;
        }

        int maximumOutputSamples = rateConverter.GetMaximumOutputSampleCount(samples.Length);
        if (maximumOutputSamples == 0)
        {
            rateConverter.Convert(samples.Span, Span<short>.Empty);
            return;
        }

        short[] buffer = ArrayPool<short>.Shared.Rent(maximumOutputSamples);
        try
        {
            int convertedSamples = rateConverter.Convert(
                samples.Span,
                buffer.AsSpan(0, maximumOutputSamples));
            await WriteConvertedAsync(
                buffer,
                convertedSamples,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<short>.Shared.Return(buffer);
        }
    }

    private async ValueTask WriteUnconvertedAsync(
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken)
    {
        if (samples.Length == 0)
            return;
        short[] buffer = ArrayPool<short>.Shared.Rent(samples.Length);
        try
        {
            samples.Span.CopyTo(buffer);
            await PcmWriteProgressWatchdog.WriteAllAsync(
                this,
                buffer,
                samples.Length,
                writeNoProgressTimeout,
                "CoreAudio output stopped consuming audio for two seconds.",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<short>.Shared.Return(buffer);
        }
    }

    private async ValueTask WriteConvertedAsync(
        short[] buffer,
        int sampleCount,
        CancellationToken cancellationToken)
    {
        await PcmWriteProgressWatchdog.WriteAllAsync(
            this,
            buffer,
            sampleCount,
            writeNoProgressTimeout,
            "CoreAudio output stopped consuming audio for two seconds.",
            cancellationToken).ConfigureAwait(false);
    }

    int IPcmWriteTarget.Write(short[] samples, int count)
        => api.WriteStream(stream, samples, count);

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
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
            throw new TimeoutException("macOS audio playback did not drain within five seconds.");
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
}
