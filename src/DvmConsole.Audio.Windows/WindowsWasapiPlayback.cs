using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DvmConsole.Audio;

[SupportedOSPlatform("windows")]
internal sealed class WindowsWasapiPlayback :
    IAudioPlayback,
    IAudioPlaybackContinuityDiagnostics,
    IAudioPlaybackCallbackDiagnostics
{
    private const int RequestedLatencyMilliseconds = 80;
    private const string MmcssTaskName = "Audio";

    private readonly MMDevice device;
    private readonly WasapiPlayer player;
    private readonly BufferedWaveProvider buffer;
    private readonly WindowsPlaybackObserver playbackObserver;
    private bool disposed;
    private Exception? playbackFailure;

    public WindowsWasapiPlayback(MMDevice device, PcmAudioFormat format)
    {
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        Format = format ?? throw new ArgumentNullException(nameof(format));
        buffer = new BufferedWaveProvider(
            new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels),
            null)
        {
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };
        playbackObserver = new WindowsPlaybackObserver(buffer, format);

        WasapiPlayer? createdPlayer = null;
        try
        {
            createdPlayer = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(RequestedLatencyMilliseconds)
                .WithMmcssThreadPriority(MmcssTaskName)
                .Build();
            createdPlayer.Init(playbackObserver);
            createdPlayer.PlaybackStopped += HandlePlaybackStopped;
            createdPlayer.Play();
            player = createdPlayer;
        }
        catch
        {
            createdPlayer?.Dispose();
            throw;
        }
    }

    public PcmAudioFormat Format { get; }
    public int? QueuedSamples => buffer.BufferedBytes / sizeof(short);
    public TimeSpan StarvedDuration => playbackObserver.StarvedDuration;
    public TimeSpan PendingStarvedDuration => playbackObserver.PendingStarvedDuration;
    public long OutputCallbackCount => playbackObserver.OutputCallbackCount;

    public void EndExpectedPlayback()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        playbackObserver.EndExpectedPlayback();
    }

    public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfPlaybackFailed();
        if (!samples.IsEmpty)
        {
            buffer.AddSamples(MemoryMarshal.AsBytes(samples.Span));
            playbackObserver.ResumePlaybackContinuity();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfPlaybackFailed();
        buffer.ClearBuffer();
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
            while (buffer.BufferedBytes > 0)
            {
                ThrowIfPlaybackFailed();
                await Task.Delay(5, timeout.Token).ConfigureAwait(false);
            }
            ThrowIfPlaybackFailed();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Windows WASAPI playback did not drain within five seconds.");
        }

        return initialSamples - (QueuedSamples ?? 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        disposed = true;
        try
        {
            await player.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            player.PlaybackStopped -= HandlePlaybackStopped;
            device.Dispose();
        }
    }

    private void HandlePlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (!disposed && args.Exception is not null)
            Interlocked.CompareExchange(ref playbackFailure, args.Exception, null);
    }

    private void ThrowIfPlaybackFailed()
    {
        Exception? failure = Volatile.Read(ref playbackFailure);
        if (failure is not null)
            throw new IOException("Windows WASAPI playback stopped unexpectedly.", failure);
    }
}

// Observes WASAPI's actual pulls from the application buffer. Endpoint padding
// is intentionally excluded: the player is continuously supplied with silence,
// while DVM Console's mixer targets only the queued application samples.
internal sealed class WindowsPlaybackObserver :
    IWaveProvider,
    IAudioPlaybackContinuityDiagnostics,
    IAudioPlaybackCallbackDiagnostics
{
    private readonly BufferedWaveProvider inner;
    private readonly int samplesPerSecond;
    private int continuityExpected;
    private long pendingStarvedSamples;
    private long starvedSamples;
    private long outputCallbackCount;

    public WindowsPlaybackObserver(BufferedWaveProvider inner, PcmAudioFormat format)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(format);
        samplesPerSecond = checked(format.SampleRate * format.Channels);
    }

    public WaveFormat WaveFormat => inner.WaveFormat;
    public TimeSpan StarvedDuration => TimeSpan.FromSeconds(
        Interlocked.Read(ref starvedSamples) / (double)samplesPerSecond);
    public TimeSpan PendingStarvedDuration => TimeSpan.FromSeconds(
        Interlocked.Read(ref pendingStarvedSamples) / (double)samplesPerSecond);
    public long OutputCallbackCount => Interlocked.Read(ref outputCallbackCount);

    public int Read(Span<byte> output)
    {
        int availableBytes = inner.BufferedBytes;
        int read = inner.Read(output);
        Interlocked.Increment(ref outputCallbackCount);

        if (Volatile.Read(ref continuityExpected) != 0 && availableBytes < read)
        {
            int missingSamples = (read - availableBytes) / sizeof(short);
            Interlocked.Add(ref pendingStarvedSamples, missingSamples);
        }
        return read;
    }

    public void ResumePlaybackContinuity()
    {
        int wasExpected = Interlocked.Exchange(ref continuityExpected, 1);
        long pending = Interlocked.Exchange(ref pendingStarvedSamples, 0);
        if (wasExpected != 0 && pending > 0)
            Interlocked.Add(ref starvedSamples, pending);
    }

    public void EndExpectedPlayback()
    {
        Volatile.Write(ref continuityExpected, 0);
        Interlocked.Exchange(ref pendingStarvedSamples, 0);
    }
}
