using DvmConsole.Audio;

namespace DvmConsole.Desktop;

/// <summary>
/// Plays one completed local recording through the configured portable output
/// backend. The coordinator owns only its playback stream; receive routing and
/// recording catalog lifetime remain separate.
/// </summary>
public sealed class RecordingPlaybackCoordinator : IAsyncDisposable
{
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
        lock (this)
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
                lock (this)
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
        lock (this)
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
        try
        {
            short[] input = new short[1600];
            while (true)
            {
                int sampleCount = await session.Reader.ReadSamplesAsync(
                    input,
                    session.Cancellation.Token).ConfigureAwait(false);
                if (sampleCount == 0)
                    break;

                short[] output = session.RateConverter?.Convert(input.AsSpan(0, sampleCount))
                    ?? input.AsSpan(0, sampleCount).ToArray();
                if (output.Length > 0)
                {
                    await session.Playback.WriteAsync(output, session.Cancellation.Token).ConfigureAwait(false);
                }
            }
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

            lock (this)
            {
                if (ReferenceEquals(activeSession, session))
                {
                    activeSession = null;
                    currentPath = null;
                }
            }

            if (failure is not null)
                faultHandler?.Invoke(failure);
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
        public IAudioPcmStreamReader Reader { get; } = reader;
        public IAudioPlayback Playback { get; } = playback;
        public PcmRateConverter? RateConverter { get; } = rateConverter;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? RunTask { get; set; }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Cancellation.Cancel();
            if (RunTask is not null)
                await RunTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
                await DisposeResourcesAsync().ConfigureAwait(false);
        }

        public async Task DisposeResourcesAsync()
        {
            try
            {
                await Playback.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await Reader.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    Cancellation.Dispose();
                }
            }
        }
    }
}
