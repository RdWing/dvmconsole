// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

public sealed record WebStreamPlaybackDescriptor(
    WebStreamId Id,
    string Name,
    string Url,
    string AuthUsername,
    string AuthPassword,
    double Volume,
    string? OutputDeviceId);

public sealed record WebStreamPlaybackState(
    WebStreamId Id,
    bool IsActive,
    bool IsConnecting,
    bool IsReceiving,
    bool IsFailed,
    string Status);

// Plays configured HTTP(S) streams through the portable PCM audio boundary.
// Supported sources include PCM WAV, MPEG/MP3, and Ogg Opus. No external
// decoder process or platform-specific media framework is assumed.
public sealed class WebStreamPlaybackCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AsyncDisposal disposal = new();
    private readonly WebStreamPlaybackRegistry registry = new();
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<string?> getOutputDeviceId;
    private readonly Func<WebStreamPlaybackDescriptor, CancellationToken, Task<Stream>> openStream;
    private readonly Func<Stream, CancellationToken, Task<IAudioPcmStreamReader>> createDecoder;
    private readonly WebStreamPlaybackStatePublisher statePublisher;
    private readonly WebStreamOutputRoutePool outputRoutes = new();
    private IAudioBackend? audioBackend;
    private bool disposed;

    public WebStreamPlaybackCoordinator(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        Func<WebStreamPlaybackDescriptor, CancellationToken, Task<Stream>>? openStream = null,
        Func<Stream, CancellationToken, Task<IAudioPcmStreamReader>>? createDecoder = null,
        Func<WebStreamPlaybackState, ValueTask>? stateObserver = null)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.getOutputDeviceId = getOutputDeviceId ?? throw new ArgumentNullException(nameof(getOutputDeviceId));
        this.openStream = openStream ?? HttpWebStreamSource.OpenAsync;
        this.createDecoder = createDecoder ?? PcmStreamDecoder.OpenAsync;
        statePublisher = new WebStreamPlaybackStatePublisher(
            stateObserver ?? (_ => ValueTask.CompletedTask));
    }

    public IReadOnlyList<WebStreamId> ActiveStreamIds => registry.ActiveStreamIds;

    public bool IsActive(WebStreamId streamId)
        => registry.IsActive(streamId);

    public async Task StartAsync(
        WebStreamPlaybackDescriptor stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        WebStreamPendingStart pending;
        IAudioBackend backend;
        AudioDeviceInfo output;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (registry.ContainsOrPending(stream.Id))
                return;

            bool createdBackend = audioBackend is null;
            backend = audioBackend ?? await Task.Run(
                    createAudioBackend,
                    CancellationToken.None)
                .ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? requestedOutput = stream.OutputDeviceId ?? getOutputDeviceId();
                output = await Task.Run(
                        () => ResolveOutputDevice(backend, requestedOutput),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (createdBackend)
                    audioBackend = backend;
            }
            catch
            {
                if (createdBackend)
                    backend.Dispose();
                throw;
            }
            pending = new WebStreamPendingStart(cancellationToken);
            registry.AddPending(stream.Id, pending);
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
        WebStreamPlaybackSession? preparedSession = null;
        bool published = false;
        try
        {
            source = await openStream(stream, pending.Token).ConfigureAwait(false);
            reader = await createDecoder(source, pending.Token).ConfigureAwait(false);
            source = null;
            playback = await outputRoutes.AcquireAsync(
                    backend,
                    output,
                    PcmAudioFormat.Voice8KhzMono16Bit,
                    $"Web stream: {stream.Name}",
                    pending.Token)
                .ConfigureAwait(false);

            preparedSession = new WebStreamPlaybackSession(
                reader,
                playback,
                reader.SampleRate == PcmAudioFormat.Voice8KhzMono16Bit.SampleRate
                    ? null
                    : new PcmRateConverter(reader.SampleRate, PcmAudioFormat.Voice8KhzMono16Bit.SampleRate));
            preparedSession.SetVolume(NormalizeVolume(stream.Volume));
            reader = null;
            playback = null;

            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (disposed || pending.IsCancellationRequested ||
                    !registry.IsCurrentPending(stream.Id, pending))
                {
                    throw new OperationCanceledException(pending.Token);
                }

                registry.RemovePending(stream.Id, pending);
                registry.AddSession(stream.Id, preparedSession);
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
                registry.RemovePending(stream.Id, pending);
                if (!published && registry.SessionCount == 0 && registry.PendingCount == 0)
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

    public async Task StopAsync(WebStreamId streamId, CancellationToken cancellationToken = default)
    {
        WebStreamPendingStart? pending = null;
        WebStreamPlaybackSession? session = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (registry.TryGetPending(streamId, out pending))
                pending?.Cancel();
            registry.TryRemoveSession(streamId, out session);
        }
        finally
        {
            gate.Release();
        }

        if (pending is not null || session is not null)
        {
            await SetPlaybackStateAsync(
                streamId,
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
        if (pending is not null || session is not null)
        {
            await SetPlaybackStateAsync(
                streamId,
                false,
                false,
                false,
                false,
                "Off").ConfigureAwait(false);
        }
    }

    public void SetVolume(WebStreamId streamId, double volume)
        => registry.SetVolume(streamId, NormalizeVolume(volume));

    // Releases the cached backend after all sessions have stopped so an audio
    // processing-mode change cannot retain a facade for the previous route.
    public async Task ResetAudioBackendAsync(CancellationToken cancellationToken = default)
    {
        IAudioBackend? oldBackend;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (registry.SessionCount != 0 || registry.PendingCount != 0)
            {
                throw new InvalidOperationException(
                    "Web-stream playback must stop before its audio route is reset.");
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

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        WebStreamPendingStart[] oldPending;
        WebStreamPlaybackSession[] oldSessions;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;
            oldPending = registry.TakeAllPending();
            foreach (WebStreamPendingStart pending in oldPending)
                pending.Cancel();
            oldSessions = registry.TakeAllSessions();
        }
        finally
        {
            gate.Release();
        }

        Exception? failure = null;
        foreach (WebStreamPendingStart pending in oldPending)
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
        foreach (WebStreamPlaybackSession session in oldSessions)
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

        try
        {
            await outputRoutes.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        IAudioBackend? oldBackend = audioBackend;
        audioBackend = null;
        oldBackend?.Dispose();
        gate.Dispose();
        if (failure is not null)
            throw failure;
    }

    private async Task RunAsync(
        WebStreamPlaybackDescriptor stream,
        WebStreamPlaybackSession session)
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
        catch (Exception) when (session.Cancellation.IsCancellationRequested)
        {
            // Disposal is allowed to interrupt a decoder that did not observe
            // cancellation promptly. That interruption belongs to the stop,
            // not to the stream's operator-visible failure state.
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            canceled = session.Cancellation.IsCancellationRequested;
            if (!canceled)
            {
                try
                {
                    await session.Playback.FlushAsync().ConfigureAwait(false);
                    await session.Playback.DrainAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            try
            {
                await session.DisposeResourcesAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            try
            {
                await RemoveCompletedAsync(stream, session).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

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
        WebStreamPlaybackDescriptor stream,
        bool active,
        bool connecting,
        bool receiving,
        bool failed,
        string status)
        => statePublisher.PublishAsync(
            stream,
            active,
            connecting,
            receiving,
            failed,
            status);

    private ValueTask SetPlaybackStateAsync(
        WebStreamId streamId,
        bool active,
        bool connecting,
        bool receiving,
        bool failed,
        string status)
        => statePublisher.PublishAsync(
            streamId,
            active,
            connecting,
            receiving,
            failed,
            status);

    private async Task RemoveCompletedAsync(
        WebStreamPlaybackDescriptor stream,
        WebStreamPlaybackSession session)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            registry.RemoveSession(stream.Id, session);
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
        WebStreamPlaybackSession? session,
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

}
