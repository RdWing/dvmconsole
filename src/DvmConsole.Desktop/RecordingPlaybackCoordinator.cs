using DvmConsole.Audio;

namespace DvmConsole.Desktop;

// Plays one completed local recording through the configured portable output
// backend. The coordinator owns only its playback stream; receive routing and
// recording catalog lifetime remain separate.
public sealed class RecordingPlaybackCoordinator : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<string?> getOutputDeviceId;
    private readonly Action<Exception>? faultHandler;
    private IAudioBackend? audioBackend;
    private PlaybackSession? activeSession;
    private string? currentPath;
    private bool disposed;

    public RecordingPlaybackCoordinator(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        Action<Exception>? faultHandler = null)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ?? throw new ArgumentNullException(nameof(getOutputDeviceId));
        this.faultHandler = faultHandler;
    }

    public bool IsPlaying(string? path = null)
    {
        lock (sync)
        {
            return activeSession is not null &&
                (path is null || string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task StartAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The recording file was not found.", fullPath);

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
                IAudioBackend backend = audioBackend ??= CreateBackend(out createdBackend);
                AudioDeviceInfo output = ResolveOutputDevice(backend, getOutputDeviceId());
                source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                reader = await PcmStreamDecoder.OpenAsync(source, cancellationToken).ConfigureAwait(false);
                source = null;
                playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit);

                var session = new PlaybackSession(
                    reader,
                    playback,
                    reader.SampleRate == PcmAudioFormat.Voice8KhzMono16Bit.SampleRate
                        ? null
                        : new PcmRateConverter(reader.SampleRate, PcmAudioFormat.Voice8KhzMono16Bit.SampleRate));
                reader = null;
                playback = null;
                lock (sync)
                {
                    activeSession = session;
                    currentPath = fullPath;
                }

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

    public async Task<bool> StopIfPlayingAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PlaybackSession? session;
            lock (sync)
            {
                if (activeSession is null ||
                    !string.Equals(currentPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                session = activeSession;
                activeSession = null;
                currentPath = null;
            }

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
        lock (sync)
        {
            session = activeSession;
            activeSession = null;
            currentPath = null;
        }

        if (session is not null)
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
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
                session.Cancellation.Token).ConfigureAwait(false);
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

            lock (sync)
            {
                if (ReferenceEquals(activeSession, session))
                {
                    activeSession = null;
                    currentPath = null;
                }
            }

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
               ?? devices.FirstOrDefault()
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
        PcmRateConverter? rateConverter)
    {
        private readonly object sync = new();
        private bool cancellationDisposed;

        public IAudioPcmStreamReader Reader { get; } = reader;
        public IAudioPlayback Playback { get; } = playback;
        public PcmRateConverter? RateConverter { get; } = rateConverter;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? RunTask { get; set; }

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
