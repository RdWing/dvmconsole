using System.Net.Http.Headers;
using System.Text;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Desktop;

// Plays configured HTTP(S) streams through the portable PCM audio boundary.
// Supported sources include uncompressed PCM WAV and MPEG/MP3. Additional
// formats can opt into the explicit FFmpeg process adapter through
// `DVM_FFMPEG`; no platform-specific media framework is assumed.
public sealed class WebStreamPlaybackCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<WebStreamViewModel, PlaybackSession> sessions = [];
    private readonly Dictionary<WebStreamViewModel, PendingStart> pendingStarts = [];
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<string?> getOutputDeviceId;
    private readonly Func<WebStreamViewModel, string?>? getStreamOutputDeviceId;
    private readonly Func<WebStreamConfiguration, CancellationToken, Task<Stream>> openStream;
    private readonly Func<Stream, CancellationToken, Task<IAudioPcmStreamReader>> createDecoder;
    private readonly IUiDispatcher uiDispatcher;
    private IAudioBackend? audioBackend;
    private bool disposed;

    public WebStreamPlaybackCoordinator()
        : this(
            () => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            () => "default")
    {
    }

    public WebStreamPlaybackCoordinator(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        Func<WebStreamConfiguration, CancellationToken, Task<Stream>>? openStream = null,
        Func<Stream, CancellationToken, Task<IAudioPcmStreamReader>>? createDecoder = null,
        Func<WebStreamViewModel, string?>? getStreamOutputDeviceId = null)
        : this(
            createAudioBackend,
            getOutputDeviceId,
            openStream,
            createDecoder,
            getStreamOutputDeviceId,
            AvaloniaUiDispatcher.Instance)
    {
    }

    internal WebStreamPlaybackCoordinator(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        Func<WebStreamConfiguration, CancellationToken, Task<Stream>>? openStream,
        Func<Stream, CancellationToken, Task<IAudioPcmStreamReader>>? createDecoder,
        Func<WebStreamViewModel, string?>? getStreamOutputDeviceId,
        IUiDispatcher uiDispatcher)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ?? throw new ArgumentNullException(nameof(getOutputDeviceId));
        this.getStreamOutputDeviceId = getStreamOutputDeviceId;
        this.openStream = openStream ?? OpenHttpStreamAsync;
        this.createDecoder = createDecoder ?? PcmStreamDecoder.OpenAsync;
        this.uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public IReadOnlyList<WebStreamViewModel> ActiveStreams
    {
        get
        {
            lock (sessions)
                return sessions.Keys.ToArray();
        }
    }

    public bool IsActive(WebStreamViewModel stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (sessions)
            return sessions.ContainsKey(stream);
    }

    public async Task StartAsync(WebStreamViewModel stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PendingStart pending;
        IAudioBackend backend;
        AudioDeviceInfo output;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sessions)
            {
                if (sessions.ContainsKey(stream) || pendingStarts.ContainsKey(stream))
                    return;
            }

            backend = audioBackend ??= createAudioBackend();
            output = ResolveOutputDevice(
                backend,
                getStreamOutputDeviceId?.Invoke(stream) ?? getOutputDeviceId());
            pending = new PendingStart(cancellationToken);
            pendingStarts.Add(stream, pending);
        }
        finally
        {
            gate.Release();
        }

        await SetPlaybackStateAsync(
            stream,
            true,
            true,
            false,
            false,
            "Connecting…").ConfigureAwait(false);
        Stream? source = null;
        IAudioPcmStreamReader? reader = null;
        IAudioPlayback? playback = null;
        PlaybackSession? preparedSession = null;
        bool published = false;
        try
        {
            source = await openStream(ToConfiguration(stream), pending.Token).ConfigureAwait(false);
            reader = await createDecoder(source, pending.Token).ConfigureAwait(false);
            source = null;
            playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit);

            preparedSession = new PlaybackSession(
                reader,
                new GainAudioPlayback(playback),
                reader.SampleRate == PcmAudioFormat.Voice8KhzMono16Bit.SampleRate
                    ? null
                    : new PcmRateConverter(reader.SampleRate, PcmAudioFormat.Voice8KhzMono16Bit.SampleRate));
            preparedSession.Playback.Gain = stream.Volume;
            reader = null;
            playback = null;

            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (disposed || pending.IsCancellationRequested ||
                    !pendingStarts.TryGetValue(stream, out PendingStart? current) ||
                    !ReferenceEquals(current, pending))
                {
                    throw new OperationCanceledException(pending.Token);
                }

                pendingStarts.Remove(stream);
                lock (sessions)
                    sessions.Add(stream, preparedSession);
                preparedSession.RunTask = RunAsync(stream, preparedSession);
                published = true;
                preparedSession = null;
            }
            finally
            {
                gate.Release();
            }

            await SetPlaybackStateAsync(
                stream,
                true,
                false,
                false,
                false,
                "Connected; waiting for audio").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested || disposed)
        {
            await DisposePreparedStartAsync(preparedSession, playback, reader, source).ConfigureAwait(false);
            await SetPlaybackStateAsync(
                stream,
                false,
                false,
                false,
                false,
                "Off").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await DisposePreparedStartAsync(preparedSession, playback, reader, source).ConfigureAwait(false);
            await SetPlaybackStateAsync(
                stream,
                false,
                false,
                false,
                true,
                CreateFailureStatus(exception)).ConfigureAwait(false);
        }
        finally
        {
            IAudioBackend? unusedBackend = null;
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (pendingStarts.TryGetValue(stream, out PendingStart? current) &&
                    ReferenceEquals(current, pending))
                {
                    pendingStarts.Remove(stream);
                }

                bool noSessions;
                lock (sessions)
                    noSessions = sessions.Count == 0;
                if (!published && noSessions && pendingStarts.Count == 0)
                {
                    unusedBackend = audioBackend;
                    audioBackend = null;
                }
            }
            finally
            {
                gate.Release();
            }

            unusedBackend?.Dispose();
            pending.Complete();
            pending.Dispose();
        }
    }

    public async Task StopAsync(WebStreamViewModel stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PendingStart? pending = null;
        PlaybackSession? session = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pendingStarts.TryGetValue(stream, out pending))
                pending.Cancel();
            lock (sessions)
                sessions.Remove(stream, out session);
        }
        finally
        {
            gate.Release();
        }

        if (pending is not null || session is not null)
        {
            await SetPlaybackStateAsync(
                stream,
                false,
                false,
                false,
                false,
                "Stopping…").ConfigureAwait(false);
        }
        if (pending is not null)
            await pending.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (session is not null)
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        if (session is not null)
        {
            await SetPlaybackStateAsync(
                stream,
                false,
                false,
                false,
                false,
                "Off").ConfigureAwait(false);
        }
    }

    public void SetVolume(WebStreamViewModel stream, double volume)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (sessions)
        {
            if (sessions.TryGetValue(stream, out PlaybackSession? session))
                session.Playback.Gain = NormalizeVolume(volume);
        }
    }

    // Releases the cached backend after all sessions have stopped so an audio
    // processing-mode change cannot retain a facade for the previous route.
    public async Task ResetAudioBackendAsync(CancellationToken cancellationToken = default)
    {
        IAudioBackend? oldBackend;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sessions)
            {
                if (sessions.Count != 0 || pendingStarts.Count != 0)
                    throw new InvalidOperationException("Web-stream playback must stop before its audio route is reset.");
            }
            oldBackend = audioBackend;
            audioBackend = null;
        }
        finally
        {
            gate.Release();
        }
        oldBackend?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        PendingStart[] oldPending;
        PlaybackSession[] oldSessions;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;
            oldPending = pendingStarts.Values.ToArray();
            pendingStarts.Clear();
            foreach (PendingStart pending in oldPending)
                pending.Cancel();
            lock (sessions)
            {
                oldSessions = sessions.Values.ToArray();
                sessions.Clear();
            }
        }
        finally
        {
            gate.Release();
        }

        Exception? failure = null;
        foreach (PendingStart pending in oldPending)
        {
            try
            {
                await pending.Completion.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }
        foreach (PlaybackSession session in oldSessions)
        {
            try
            {
                await session.StopAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        IAudioBackend? oldBackend = audioBackend;
        audioBackend = null;
        oldBackend?.Dispose();
        gate.Dispose();
        if (failure is not null)
            throw failure;
    }

    private async Task RunAsync(WebStreamViewModel stream, PlaybackSession session)
    {
        Exception? failure = null;
        bool canceled = false;
        try
        {
            await PcmPlaybackPump.RunAsync(
                session.Reader,
                session.Playback,
                session.RateConverter,
                session.Cancellation.Token,
                () => SetPlaybackStateAsync(
                        stream,
                        true,
                        false,
                        true,
                        false,
                        "Receiving"))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            // Expected when an operator stops the stream or the window closes.
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            canceled = session.Cancellation.IsCancellationRequested;
            try
            {
                await session.Playback.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            await session.DisposeResourcesAsync().ConfigureAwait(false);
            await RemoveCompletedAsync(stream, session).ConfigureAwait(false);

            if (failure is not null)
            {
                await SetPlaybackStateAsync(
                    stream,
                    false,
                    false,
                    false,
                    true,
                    CreateFailureStatus(failure)).ConfigureAwait(false);
            }
            else if (!canceled)
            {
                await SetPlaybackStateAsync(
                    stream,
                    false,
                    false,
                    false,
                    false,
                    "Ended").ConfigureAwait(false);
            }
        }
    }

    private ValueTask SetPlaybackStateAsync(
        WebStreamViewModel stream,
        bool active,
        bool connecting,
        bool receiving,
        bool failed,
        string status)
        => uiDispatcher.InvokeAsync(() => stream.SetPlaybackState(
            active,
            connecting,
            receiving,
            failed,
            status));

    private async Task RemoveCompletedAsync(WebStreamViewModel stream, PlaybackSession session)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sessions)
            {
                if (sessions.TryGetValue(stream, out PlaybackSession? current) && ReferenceEquals(current, session))
                    sessions.Remove(stream);
            }
        }
        finally
        {
            gate.Release();
        }
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

    private static WebStreamConfiguration ToConfiguration(WebStreamViewModel stream)
        => new()
        {
            Name = stream.Name,
            Url = stream.Url,
            AuthUsername = stream.AuthUsername,
            AuthPassword = stream.AuthPassword
        };

    private static double NormalizeVolume(double volume)
        => double.IsFinite(volume) ? Math.Clamp(volume, 0, 4) : 1.0;

    private static string CreateFailureStatus(Exception exception)
        => exception is NotSupportedException
            ? $"Unsupported: {exception.Message}"
            : $"Failed: {exception.Message}";

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

    private static async Task DisposePreparedStartAsync(
        PlaybackSession? session,
        IAudioPlayback? playback,
        IAudioPcmStreamReader? reader,
        Stream? source)
    {
        if (session is not null)
        {
            await session.DisposeResourcesAsync().ConfigureAwait(false);
            return;
        }

        await DisposeIfCreatedAsync(playback, reader, source).ConfigureAwait(false);
    }

    private static async Task<Stream> OpenHttpStreamAsync(
        WebStreamConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(configuration.Url, UriKind.Absolute, out Uri? uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The web stream URL must be an absolute HTTP or HTTPS URL.");
        }

        var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(configuration.AuthUsername))
        {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{configuration.AuthUsername}:{configuration.AuthPassword ?? string.Empty}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            request.Dispose();
            return new HttpResponseStream(client, response, content);
        }
        catch
        {
            request.Dispose();
            response?.Dispose();
            client.Dispose();
            throw;
        }
    }

    private sealed class PlaybackSession(
        IAudioPcmStreamReader reader,
        GainAudioPlayback playback,
        PcmRateConverter? rateConverter)
    {
        public IAudioPcmStreamReader Reader { get; } = reader;
        public GainAudioPlayback Playback { get; } = playback;
        public PcmRateConverter? RateConverter { get; } = rateConverter;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? RunTask { get; set; }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The stream may have completed and disposed its cancellation source
                // just before the operator requested Stop.
            }
            Task? task = RunTask;
            if (task is not null)
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    private sealed class PendingStart(CancellationToken cancellationToken) : IDisposable
    {
        private readonly CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token => cancellation.Token;
        public bool IsCancellationRequested => cancellation.IsCancellationRequested;
        public Task Completion => completion.Task;

        public void Cancel()
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Complete() => completion.TrySetResult();

        public void Dispose() => cancellation.Dispose();
    }

    private sealed class GainAudioPlayback(IAudioPlayback inner) : IAudioPlayback, IAudioGainControl
    {
        private readonly object sync = new();
        private bool disposed;
        private double gain = 1.0;

        public PcmAudioFormat Format => inner.Format;

        public double Gain
        {
            get
            {
                lock (sync)
                    return gain;
            }
            set
            {
                if (!double.IsFinite(value) || value is < 0 or > 4)
                    throw new ArgumentOutOfRangeException(nameof(value), "Audio gain must be between 0 and 4.");
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    gain = value;
                }
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            double currentGain;
            lock (sync)
                currentGain = gain;
            if (Math.Abs(currentGain - 1.0) < 0.0001)
                return inner.WriteAsync(samples, cancellationToken);

            var scaled = new short[samples.Length];
            for (int index = 0; index < scaled.Length; index++)
            {
                int value = (int)Math.Round(samples.Span[index] * currentGain, MidpointRounding.AwayFromZero);
                scaled[index] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            }

            return inner.WriteAsync(scaled, cancellationToken);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => inner.FlushAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            bool shouldDispose;
            lock (sync)
            {
                shouldDispose = !disposed;
                disposed = true;
            }

            if (shouldDispose)
                await inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class HttpResponseStream(
        HttpClient client,
        HttpResponseMessage response,
        Stream inner) : Stream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            client.Dispose();
            GC.SuppressFinalize(this);
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
