// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using ApplicationWebStreamPlaybackCoordinator =
    DvmConsole.Application.WebStreamPlaybackCoordinator;

namespace DvmConsole.Desktop;

// Desktop presentation adapter. The Application service owns network/audio
// sessions; this type only maps view models and UI-thread state publication.
public sealed class WebStreamPlaybackCoordinator : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<WebStreamId, WebStreamViewModel> streams = [];
    private readonly Dictionary<WebStreamId, WebStreamPlaybackState> pendingStates = [];
    private readonly CoalescedUiAction presentation;
    private readonly IUiDispatcher uiDispatcher;
    private readonly Func<WebStreamViewModel, string?>? getStreamOutputDeviceId;
    private readonly ApplicationWebStreamPlaybackCoordinator inner;
    private bool disposed;
    private readonly bool ownsPlayback;

    public WebStreamPlaybackCoordinator()
        : this(
            CreateDefaultAudioBackend,
            () => "default")
    {
    }

    private static IAudioBackend CreateDefaultAudioBackend()
        => new DesktopAudioBackendFactory(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY"))
            .Create(AudioBackendConfiguration.Default);

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
        : this(observer => new ApplicationWebStreamPlaybackCoordinator(
                createAudioBackend, getOutputDeviceId,
                openStream is null ? null : (descriptor, token) => openStream(ToConfiguration(descriptor), token),
                createDecoder, observer), getStreamOutputDeviceId, uiDispatcher, ownsPlayback: true)
    {
    }

    internal WebStreamPlaybackCoordinator(
        Func<Func<WebStreamPlaybackState, ValueTask>, ApplicationWebStreamPlaybackCoordinator> createRuntime,
        Func<WebStreamViewModel, string?>? getStreamOutputDeviceId,
        IUiDispatcher uiDispatcher, bool ownsPlayback = false)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        this.uiDispatcher = uiDispatcher;
        this.getStreamOutputDeviceId = getStreamOutputDeviceId;
        this.ownsPlayback = ownsPlayback;
        presentation = new(uiDispatcher, ApplyPendingStates, ReportPresentationFailure);
        inner = createRuntime(QueueState);
    }

    public IReadOnlyList<WebStreamViewModel> ActiveStreams
    {
        get
        {
            lock (sync)
                return inner.ActiveStreamIds
                    .Select(id => streams.GetValueOrDefault(id))
                    .Where(stream => stream is not null)
                    .Cast<WebStreamViewModel>()
                    .ToArray();
        }
    }

    public bool IsActive(WebStreamViewModel stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return inner.IsActive(GetId(stream));
    }

    public async Task StartAsync(
        WebStreamViewModel stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        WebStreamId id = GetId(stream);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            streams[id] = stream;
        }
        await inner.StartAsync(ToDescriptor(stream, id), cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(
        WebStreamViewModel stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return inner.StopAsync(GetId(stream), cancellationToken);
    }

    public void SetVolume(WebStreamViewModel stream, double volume)
    {
        ArgumentNullException.ThrowIfNull(stream);
        inner.SetVolume(GetId(stream), volume);
    }

    public Task ResetAudioBackendAsync(CancellationToken cancellationToken = default)
        => inner.ResetAudioBackendAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        WebStreamViewModel[] retiring;
        lock (sync)
        {
            if (disposed) return DisposePlaybackAsync();
            disposed = true;
            retiring = streams.Values.ToArray();
            pendingStates.Clear();
            streams.Clear();
        }
        presentation.Dispose();
        void PublishRetired()
        {
            foreach (WebStreamViewModel stream in retiring)
            {
                try { stream.SetPlaybackState(false, false, false, false, "Off"); }
                catch (Exception exception) { ReportPresentationFailure(exception); }
            }
        }
        try
        {
            if (uiDispatcher.CheckAccess()) PublishRetired();
            else uiDispatcher.Post(PublishRetired);
        }
        catch (Exception exception) { ReportPresentationFailure(exception); }
        return DisposePlaybackAsync();
    }

    private ValueTask DisposePlaybackAsync() => ownsPlayback ? inner.DisposeAsync() : ValueTask.CompletedTask;

    private ValueTask QueueState(WebStreamPlaybackState state)
    {
        lock (sync)
        {
            if (disposed) return ValueTask.CompletedTask;
            pendingStates[state.Id] = state;
        }
        // Playback owns network/audio state. A stalled UI retains one latest
        // update per stream without delaying startup, PCM work or retirement.
        if (uiDispatcher.CheckAccess()) ApplyPendingStates();
        else presentation.Schedule();
        return ValueTask.CompletedTask;
    }

    private void ApplyPendingStates()
    {
        WebStreamPlaybackState[] updates;
        lock (sync)
        {
            if (disposed) return;
            updates = pendingStates.Values.ToArray();
            pendingStates.Clear();
        }
        foreach (WebStreamPlaybackState state in updates)
        {
            try { ApplyState(state); }
            catch (Exception exception) { ReportPresentationFailure(exception); }
        }
    }

    private static void ReportPresentationFailure(Exception exception)
        => System.Diagnostics.Trace.TraceError("Web-stream presentation failed: {0}", exception);

    private void ApplyState(WebStreamPlaybackState state)
    {
        WebStreamViewModel? stream;
        lock (sync)
            streams.TryGetValue(state.Id, out stream);
        stream?.SetPlaybackState(
            state.IsActive,
            state.IsConnecting,
            state.IsReceiving,
            state.IsFailed,
            state.Status);
    }

    private WebStreamPlaybackDescriptor ToDescriptor(
        WebStreamViewModel stream,
        WebStreamId id)
        => new(
            id,
            stream.Name,
            stream.Url,
            stream.AuthUsername,
            stream.AuthPassword,
            stream.Volume,
            getStreamOutputDeviceId?.Invoke(stream));

    private static WebStreamId GetId(WebStreamViewModel stream)
        => WebStreamId.FromIdentity(stream.Name, stream.Url);

    private static WebStreamConfiguration ToConfiguration(
        WebStreamPlaybackDescriptor stream)
        => new()
        {
            Name = stream.Name,
            Url = stream.Url,
            AuthUsername = stream.AuthUsername,
            AuthPassword = stream.AuthPassword
        };
}
