using DvmConsole.Audio;

namespace DvmConsole.Application;

public sealed class RecordingPlaybackStateChangedEventArgs(
    RecordingId recordingId,
    bool isPlaying) : EventArgs
{
    public RecordingId RecordingId { get; } = recordingId;
    public bool IsPlaying { get; } = isPlaying;
}

public sealed record RecordingPlaybackStartupMetrics(
    RecordingId RecordingId,
    TimeSpan SourceOpen,
    TimeSpan DecoderOpen,
    TimeSpan FirstDecode,
    TimeSpan OutputOpen,
    TimeSpan FirstOutput);

/// <summary>
/// Plays completed recordings through the configured portable audio backend.
/// Recording identity and media access remain independent of filesystem paths.
/// </summary>
public sealed class RecordingPlaybackCoordinator : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IRecordingStore recordingStore;
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<string?> getOutputDeviceId;
    private readonly Action<Exception>? faultHandler;
    private readonly Action<RecordingPlaybackStartupMetrics>? startupObserver;
    private readonly TimeProvider timeProvider;
    private IAudioBackend? audioBackend;
    private PlaybackSession? activeSession;
    private RecordingId? currentRecordingId;
    private bool disposed;

    public RecordingPlaybackCoordinator(
        IRecordingStore recordingStore,
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        Action<Exception>? faultHandler = null,
        Action<RecordingPlaybackStartupMetrics>? startupObserver = null,
        TimeProvider? timeProvider = null)
    {
        this.recordingStore = recordingStore ?? throw new ArgumentNullException(nameof(recordingStore));
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ?? throw new ArgumentNullException(nameof(getOutputDeviceId));
        this.faultHandler = faultHandler;
        this.startupObserver = startupObserver;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<RecordingPlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public bool IsPlaying(RecordingId? recordingId = null)
    {
        lock (sync)
        {
            return activeSession is not null &&
                (recordingId is null || currentRecordingId == recordingId);
        }
    }

    public async Task StartAsync(
        RecordingId recordingId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await StopActiveCoreAsync(cancellationToken).ConfigureAwait(false);

            Stream? source = null;
            IAudioPcmStreamReader? reader = null;
            IAudioPlayback? playback = null;
            bool createdBackend = false;
            try
            {
                long startedAt = timeProvider.GetTimestamp();
                IAudioBackend backend = audioBackend ??= CreateBackend(out createdBackend);
                AudioDeviceInfo output = ResolveOutputDevice(backend, getOutputDeviceId());
                source = await recordingStore
                    .OpenReadAsync(recordingId, cancellationToken)
                    .ConfigureAwait(false);
                TimeSpan sourceOpen = timeProvider.GetElapsedTime(startedAt);
                reader = await PcmStreamDecoder.OpenAsync(source, cancellationToken).ConfigureAwait(false);
                source = null;
                TimeSpan decoderOpen = timeProvider.GetElapsedTime(startedAt);
                short[] prefetched = new short[PcmPlaybackPump.RecommendedPrefetchSamples];
                int prefetchedCount = await reader
                    .ReadSamplesAsync(prefetched, cancellationToken)
                    .ConfigureAwait(false);
                if (prefetchedCount == 0)
                    throw new InvalidDataException("The recording contains no playable audio samples.");
                TimeSpan firstDecode = timeProvider.GetElapsedTime(startedAt);
                playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit);
                TimeSpan outputOpen = timeProvider.GetElapsedTime(startedAt);

                var session = new PlaybackSession(
                    reader,
                    playback,
                    reader.SampleRate == PcmAudioFormat.Voice8KhzMono16Bit.SampleRate
                        ? null
                        : new PcmRateConverter(reader.SampleRate, PcmAudioFormat.Voice8KhzMono16Bit.SampleRate),
                    prefetched.AsMemory(0, prefetchedCount),
                    new RecordingPlaybackStartupMetrics(
                        recordingId,
                        sourceOpen,
                        decoderOpen,
                        firstDecode,
                        outputOpen,
                        TimeSpan.Zero),
                    startedAt);
                reader = null;
                playback = null;
                lock (sync)
                {
                    activeSession = session;
                    currentRecordingId = recordingId;
                    session.PlaybackAnnounced = true;
                }

                NotifyPlaybackStateChanged(recordingId, isPlaying: true);
                session.RunTask = RunAsync(session);
            }
            catch
            {
                await DisposeIfCreatedAsync(playback, reader, source).ConfigureAwait(false);
                if (createdBackend && !IsPlaying())
                {
                    audioBackend?.Dispose();
                    audioBackend = null;
                }

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopActiveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ResetAudioBackendAsync(CancellationToken cancellationToken = default)
    {
        IAudioBackend? oldBackend;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await StopActiveCoreAsync(cancellationToken).ConfigureAwait(false);
            oldBackend = audioBackend;
            audioBackend = null;
        }
        finally
        {
            gate.Release();
        }
        oldBackend?.Dispose();
    }

    public async Task<bool> StopIfPlayingAsync(
        RecordingId recordingId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PlaybackSession? session;
            lock (sync)
            {
                if (activeSession is null || currentRecordingId != recordingId)
                    return false;

                session = activeSession;
                activeSession = null;
                currentRecordingId = null;
            }

            if (session.PlaybackAnnounced)
                NotifyPlaybackStateChanged(recordingId, isPlaying: false);
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;
            await StopActiveCoreAsync().ConfigureAwait(false);
            audioBackend?.Dispose();
            audioBackend = null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StopActiveCoreAsync(CancellationToken cancellationToken = default)
    {
        PlaybackSession? session;
        RecordingId? recordingId;
        lock (sync)
        {
            session = activeSession;
            recordingId = currentRecordingId;
            activeSession = null;
            currentRecordingId = null;
        }

        if (session is not null && recordingId is not null)
        {
            if (session.PlaybackAnnounced)
                NotifyPlaybackStateChanged(recordingId.Value, isPlaying: false);
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(PlaybackSession session)
    {
        Exception? failure = null;
        bool completedNaturally = false;
        try
        {
            await PcmPlaybackPump.RunAsync(
                session.Reader,
                session.Playback,
                session.RateConverter,
                session.Cancellation.Token,
                () => ObserveFirstOutputAsync(session),
                prefetchedSamples: session.PrefetchedSamples).ConfigureAwait(false);
            completedNaturally = true;
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            // Expected when an operator stops playback or the application closes.
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                if (completedNaturally && failure is null)
                    await session.Playback.DrainAsync().ConfigureAwait(false);
                else
                    await session.Playback.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            try
            {
                await session.DisposeResourcesAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            RecordingId? completedRecordingId = null;
            lock (sync)
            {
                if (ReferenceEquals(activeSession, session))
                {
                    completedRecordingId = currentRecordingId;
                    activeSession = null;
                    currentRecordingId = null;
                }
            }

            if (completedRecordingId is not null && session.PlaybackAnnounced)
                NotifyPlaybackStateChanged(completedRecordingId.Value, isPlaying: false);

            try
            {
                if (failure is not null)
                    faultHandler?.Invoke(failure);
            }
            finally
            {
                session.CompleteLifecycle();
            }
        }
    }

    private void NotifyPlaybackStateChanged(RecordingId recordingId, bool isPlaying)
    {
        EventHandler<RecordingPlaybackStateChangedEventArgs>? handlers = PlaybackStateChanged;
        if (handlers is null)
            return;

        var eventArgs = new RecordingPlaybackStateChangedEventArgs(recordingId, isPlaying);
        foreach (EventHandler<RecordingPlaybackStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                ReportObserverFailure(exception);
            }
        }
    }

    private void ReportObserverFailure(Exception exception)
    {
        try
        {
            faultHandler?.Invoke(exception);
        }
        catch
        {
            // Diagnostics must not interrupt playback lifecycle cleanup.
        }
    }

    private ValueTask ObserveFirstOutputAsync(PlaybackSession session)
    {
        RecordingId? recordingId = null;
        lock (sync)
        {
            if (ReferenceEquals(activeSession, session) && currentRecordingId is RecordingId activeId)
                recordingId = activeId;
        }

        if (recordingId is null)
            return ValueTask.CompletedTask;

        TimeSpan firstOutput = timeProvider.GetElapsedTime(session.StartedAt);
        try
        {
            startupObserver?.Invoke(session.StartupMetrics with { FirstOutput = firstOutput });
        }
        catch (Exception exception)
        {
            ReportObserverFailure(exception);
        }
        return ValueTask.CompletedTask;
    }

    private IAudioBackend CreateBackend(out bool created)
    {
        IAudioBackend backend = createAudioBackend();
        created = true;
        return backend;
    }

    private static AudioDeviceInfo ResolveOutputDevice(IAudioBackend backend, string? requestedDeviceId)
    {
        IReadOnlyList<AudioDeviceInfo> devices = backend.EnumerateDevices(AudioDirection.Output);
        return devices.FirstOrDefault(device =>
                   !string.IsNullOrWhiteSpace(requestedDeviceId) &&
                   !requestedDeviceId.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                   device.Id.Equals(requestedDeviceId, StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault(device => device.IsDefault)
               ?? (devices.Count > 0 ? devices[0] : null)
               ?? throw new InvalidOperationException("No audio output device is available.");
    }

    private static async Task DisposeIfCreatedAsync(
        IAudioPlayback? playback,
        IAudioPcmStreamReader? reader,
        Stream? source)
    {
        if (playback is not null)
            await playback.DisposeAsync().ConfigureAwait(false);
        if (reader is not null)
            await reader.DisposeAsync().ConfigureAwait(false);
        if (source is not null)
            await source.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class PlaybackSession(
        IAudioPcmStreamReader reader,
        IAudioPlayback playback,
        PcmRateConverter? rateConverter,
        ReadOnlyMemory<short> prefetchedSamples,
        RecordingPlaybackStartupMetrics startupMetrics,
        long startedAt)
    {
        private readonly object sync = new();
        private bool cancellationDisposed;

        public IAudioPcmStreamReader Reader { get; } = reader;
        public IAudioPlayback Playback { get; } = playback;
        public PcmRateConverter? RateConverter { get; } = rateConverter;
        public ReadOnlyMemory<short> PrefetchedSamples { get; } = prefetchedSamples;
        public RecordingPlaybackStartupMetrics StartupMetrics { get; } = startupMetrics;
        public long StartedAt { get; } = startedAt;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? RunTask { get; set; }
        public bool PlaybackAnnounced { get; set; }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Task? runTask;
            lock (sync)
            {
                if (!cancellationDisposed)
                    Cancellation.Cancel();
                runTask = RunTask;
            }

            if (runTask is not null)
                await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
            {
                await DisposeResourcesAsync().ConfigureAwait(false);
                CompleteLifecycle();
            }
        }

        public async Task DisposeResourcesAsync()
        {
            try
            {
                await Playback.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await Reader.DisposeAsync().ConfigureAwait(false);
            }
        }

        public void CompleteLifecycle()
        {
            lock (sync)
            {
                if (cancellationDisposed)
                    return;
                Cancellation.Dispose();
                cancellationDisposed = true;
            }
        }
    }
}
