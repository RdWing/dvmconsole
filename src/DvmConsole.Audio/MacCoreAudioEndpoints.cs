using System.Buffers;
using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

internal sealed class MacCoreAudioCapture : IAudioCapture
{
    private readonly NativeCoreAudioApi api;
    private readonly SafeCoreAudioStreamHandle stream;
    private readonly PcmRateConverter? rateConverter;
    private readonly bool highQualitySessionAcquired;
    private CancellationTokenSource? pumpCancellation;
    private Task? pumpTask;
    private bool disposed;

    public MacCoreAudioCapture(
        NativeCoreAudioApi api,
        ulong deviceId,
        ulong outputDeviceId,
        bool highQualityBluetoothAudio,
        PcmAudioFormat format)
    {
        MacCoreAudioBackend.ValidateVoiceFormat(format);
        this.api = api;
        Format = format;
        highQualitySessionAcquired = highQualityBluetoothAudio &&
            api.AcquireHighQualityBluetooth(deviceId, outputDeviceId) != 0;
        stream = api.CreateStream(deviceId, input: 1, format.SampleRate, format.Channels, format.BitsPerSample);
        if (stream.IsInvalid)
        {
            if (highQualitySessionAcquired)
                api.ReleaseHighQualityBluetooth();
            throw new InvalidOperationException("CoreAudio could not create the capture stream.");
        }
        int nativeSampleRate = api.GetSampleRate(stream);
        rateConverter = nativeSampleRate == format.SampleRate ? null : new PcmRateConverter(nativeSampleRate, format.SampleRate);
    }

    public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
    public PcmAudioFormat Format { get; }
    public bool IsRunning => pumpCancellation is not null;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRunning)
            return ValueTask.CompletedTask;

        MacCoreAudioBackend.EnsureSuccess(api.StartStream(stream), "start CoreAudio capture");
        pumpCancellation = new CancellationTokenSource();
        pumpTask = PumpAsync(pumpCancellation.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cancellation = pumpCancellation;
        Task? task = pumpTask;
        pumpCancellation = null;
        pumpTask = null;
        cancellation?.Cancel();
        if (task is not null)
            await task.ConfigureAwait(false);
        cancellation?.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        MacCoreAudioBackend.EnsureSuccess(api.StopStream(stream), "stop CoreAudio capture");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        await StopAsync().ConfigureAwait(false);
        try
        {
            stream.Dispose();
        }
        finally
        {
            if (highQualitySessionAcquired)
                api.ReleaseHighQualityBluetooth();
            disposed = true;
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        short[] buffer = new short[1600];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                int count = api.ReadStream(stream, buffer, buffer.Length);
                if (count <= 0)
                    continue;

                short[] samples = new short[count];
                Array.Copy(buffer, samples, count);
                if (rateConverter is not null)
                    samples = rateConverter.Convert(samples);
                if (samples.Length == 0)
                    continue;
                SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }
}

internal sealed class MacCoreAudioPlayback :
    IAudioPlayback,
    IAudioPlaybackContinuityDiagnostics,
    IAudioPlaybackCallbackDiagnostics
{
    private readonly NativeCoreAudioApi api;
    private readonly SafeCoreAudioStreamHandle stream;
    private readonly PcmRateConverter? rateConverter;
    private readonly int nativeSampleRate;
    private bool disposed;

    public MacCoreAudioPlayback(NativeCoreAudioApi api, ulong deviceId, PcmAudioFormat format)
    {
        MacCoreAudioBackend.ValidatePlaybackFormat(format);
        this.api = api;
        Format = format;
        stream = api.CreateStream(deviceId, input: 0, format.SampleRate, format.Channels, format.BitsPerSample);
        if (stream.IsInvalid)
            throw new InvalidOperationException("CoreAudio could not create the playback stream.");
        nativeSampleRate = api.GetSampleRate(stream);
        rateConverter = nativeSampleRate == format.SampleRate
            ? null
            : new PcmRateConverter(format.SampleRate, nativeSampleRate, format.Channels);
        MacCoreAudioBackend.EnsureSuccess(api.StartStream(stream), "start CoreAudio playback");
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
        short[] buffer = GetZeroOffsetArrayOrCopy(samples);
        int offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            short[] writeBuffer = offset == 0
                ? buffer
                : buffer.AsSpan(offset).ToArray();
            int written = api.WriteStream(stream, writeBuffer, buffer.Length - offset);
            MacCoreAudioBackend.EnsureSuccess(written < 0 ? written : 0, "write CoreAudio playback");
            offset += written;
            if (written == 0)
                await Task.Delay(2, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteConvertedAsync(
        short[] buffer,
        int sampleCount,
        CancellationToken cancellationToken)
    {
        int remaining = sampleCount;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int written = api.WriteStream(stream, buffer, remaining);
            MacCoreAudioBackend.EnsureSuccess(written < 0 ? written : 0, "write CoreAudio playback");
            if (written > 0)
            {
                remaining -= written;
                if (remaining > 0)
                    Array.Copy(buffer, written, buffer, 0, remaining);
            }
            else
            {
                await Task.Delay(2, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static short[] GetZeroOffsetArrayOrCopy(ReadOnlyMemory<short> samples)
    {
        if (MemoryMarshal.TryGetArray(samples, out ArraySegment<short> segment) &&
            segment.Array is short[] array &&
            segment.Offset == 0 &&
            segment.Count == array.Length)
        {
            return array;
        }
        return samples.ToArray();
    }

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
}

internal sealed class MacVoiceProcessingCapture : IAudioCapture
{
    private readonly VoiceProcessingSession session;
    private CancellationTokenSource? pumpCancellation;
    private Task? pumpTask;
    private bool disposed;

    public MacVoiceProcessingCapture(VoiceProcessingSession session, PcmAudioFormat format)
    {
        this.session = session;
        Format = format;
    }

    public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
    public PcmAudioFormat Format { get; }
    public bool IsRunning => pumpCancellation is not null;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRunning)
            return ValueTask.CompletedTask;

        session.StartCapture();
        pumpCancellation = new CancellationTokenSource();
        pumpTask = PumpAsync(pumpCancellation.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cancellation = pumpCancellation;
        Task? task = pumpTask;
        if (cancellation is null)
            return;

        pumpCancellation = null;
        pumpTask = null;
        cancellation.Cancel();
        if (task is not null)
            await task.ConfigureAwait(false);
        cancellation.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        session.StopCapture();
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
            VoiceProcessingSessionRegistry.Release(session, VoiceEndpoint.Capture);
            disposed = true;
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        short[] buffer = new short[Math.Max(1600, Format.SampleRate / 5)];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                int count = session.Read(buffer);
                if (count <= 0)
                    continue;
                short[] samples = new short[count];
                Array.Copy(buffer, samples, count);
                SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }
}

internal sealed class MacVoiceProcessingPlayback :
    IAudioPlayback,
    IAudioPlaybackContinuityDiagnostics,
    IAudioPlaybackCallbackDiagnostics
{
    private readonly VoiceProcessingSession session;
    private bool disposed;

    public MacVoiceProcessingPlayback(VoiceProcessingSession session, PcmAudioFormat format)
    {
        this.session = session;
        Format = format;
        try
        {
            session.StartPlayback();
        }
        catch
        {
            VoiceProcessingSessionRegistry.Release(session, VoiceEndpoint.Playback);
            throw;
        }
    }

    public PcmAudioFormat Format { get; }
    public int? QueuedSamples => session.QueuedSamples;
    public TimeSpan StarvedDuration => session.StarvedDuration;
    public TimeSpan PendingStarvedDuration => session.PendingStarvedDuration;
    public long OutputCallbackCount => session.OutputCallbackCount;

    public void EndExpectedPlayback()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        session.EndExpectedPlayback();
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        short[] buffer;
        if (MemoryMarshal.TryGetArray(samples, out ArraySegment<short> segment) &&
            segment.Array is short[] array &&
            segment.Offset == 0 &&
            segment.Count == array.Length)
        {
            buffer = array;
        }
        else
        {
            buffer = samples.ToArray();
        }
        int offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int written = session.Write(
                offset == 0 ? buffer : buffer.AsSpan(offset).ToArray());
            if (written < 0)
                MacCoreAudioBackend.EnsureSuccess(written, "write Apple voice-processing playback");
            offset += written;
            if (written == 0)
                await Task.Delay(2, cancellationToken).ConfigureAwait(false);
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
            throw new TimeoutException("Apple voice-processing playback did not drain within five seconds.");
        }
        return initialSamples - (QueuedSamples ?? 0);
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            try
            {
                session.StopPlayback();
            }
            finally
            {
                VoiceProcessingSessionRegistry.Release(session, VoiceEndpoint.Playback);
                disposed = true;
            }
        }
        return ValueTask.CompletedTask;
    }
}
