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
    private readonly Func<WebStreamViewModel, string?>? getStreamOutputDeviceId;
    private readonly ApplicationWebStreamPlaybackCoordinator inner;

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
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        this.getStreamOutputDeviceId = getStreamOutputDeviceId;
        inner = new ApplicationWebStreamPlaybackCoordinator(
            createAudioBackend,
            getOutputDeviceId,
            openStream is null
                ? null
                : (descriptor, cancellationToken) => openStream(
                    ToConfiguration(descriptor),
                    cancellationToken),
            createDecoder,
            state => uiDispatcher.InvokeAsync(() => ApplyState(state)));
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
            streams[id] = stream;
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

    public ValueTask DisposeAsync() => inner.DisposeAsync();

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
