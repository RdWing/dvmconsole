using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

internal sealed class MacCoreAudioCapture : IAudioCapture
{
    private readonly NativeCoreAudioApi api;
    private readonly SafeCoreAudioStreamHandle stream;
    private readonly PcmRateConverter? rateConverter;
    private CancellationTokenSource? pumpCancellation;
    private Task? pumpTask;
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
        }
        catch
        {
            createdStream?.Dispose();
            throw;
        }
    }

    public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
    public PcmAudioFormat Format { get; }
    public bool IsRunning => pumpCancellation is not null && pumpTask is { IsCompleted: false };

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
        Exception? pumpFailure = null;
        try
        {
            cancellation?.Cancel();
            if (task is not null)
                await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            pumpFailure = exception;
        }
        finally
        {
            cancellation?.Dispose();
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

                if (rateConverter is null)
                {
                    short[] samples = buffer.AsSpan(0, count).ToArray();
                    SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
                    continue;
                }

                int maximumOutputSamples = rateConverter.GetMaximumOutputSampleCount(count);
                if (maximumOutputSamples == 0)
                {
                    rateConverter.Convert(buffer.AsSpan(0, count), Span<short>.Empty);
                    continue;
                }

                var converted = new short[maximumOutputSamples];
                int convertedCount = rateConverter.Convert(
                    buffer.AsSpan(0, count),
                    converted);
                if (convertedCount > 0)
                {
                    SamplesAvailable?.Invoke(
                        this,
                        new PcmSamplesEventArgs(converted.AsMemory(0, convertedCount)));
                }
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
    IAudioPlaybackCallbackDiagnostics,
    IAudioPlaybackPresentationLatencyDiagnostics,
    IPcmWriteTarget
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
        Exception? pumpFailure = null;
        try
        {
            cancellation.Cancel();
            if (task is not null)
                await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            pumpFailure = exception;
        }
        finally
        {
            cancellation.Dispose();
            session.StopCapture();
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
    IAudioPlaybackCallbackDiagnostics,
    IAudioPlaybackPresentationLatencyDiagnostics,
    IPcmWriteTarget
{
    private static readonly TimeSpan DefaultWriteNoProgressTimeout = TimeSpan.FromSeconds(2);
    private readonly IVoiceProcessingPlaybackSession session;
    private readonly Action releaseSession;
    private readonly TimeSpan writeNoProgressTimeout;
    private bool disposed;

    public MacVoiceProcessingPlayback(VoiceProcessingSession session, PcmAudioFormat format)
        : this(
            session,
            format,
            () => VoiceProcessingSessionRegistry.Release(session, VoiceEndpoint.Playback),
            DefaultWriteNoProgressTimeout)
    {
    }

    internal MacVoiceProcessingPlayback(
        IVoiceProcessingPlaybackSession session,
        PcmAudioFormat format,
        Action? releaseSession = null,
        TimeSpan? writeNoProgressTimeout = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.releaseSession = releaseSession ?? (() => { });
        this.writeNoProgressTimeout = writeNoProgressTimeout ?? DefaultWriteNoProgressTimeout;
        if (this.writeNoProgressTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(writeNoProgressTimeout));
        Format = format;
        try
        {
            session.StartPlayback();
        }
        catch
        {
            this.releaseSession();
            throw;
        }
    }

    public PcmAudioFormat Format { get; }
    public int? QueuedSamples => session.QueuedSamples;
    public TimeSpan StarvedDuration => session.StarvedDuration;
    public TimeSpan PendingStarvedDuration => session.PendingStarvedDuration;
    public long OutputCallbackCount => session.OutputCallbackCount;
    public TimeSpan OutputPresentationLatency => session.OutputPresentationLatency;

    public void EndExpectedPlayback()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        session.EndExpectedPlayback();
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
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
                "Apple voice-processing output stopped consuming audio for two seconds.",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<short>.Shared.Return(buffer);
        }
    }

    int IPcmWriteTarget.Write(short[] samples, int count)
        => session.Write(samples, count);

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
                releaseSession();
                disposed = true;
            }
        }
        return ValueTask.CompletedTask;
    }
}
