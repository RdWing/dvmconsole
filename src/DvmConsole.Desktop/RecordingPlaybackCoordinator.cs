using System.Runtime.CompilerServices;
using DvmConsole.Application;
using DvmConsole.Audio;
using ApplicationPlaybackCoordinator = DvmConsole.Application.RecordingPlaybackCoordinator;
using ApplicationPlaybackStateChangedEventArgs = DvmConsole.Application.RecordingPlaybackStateChangedEventArgs;

namespace DvmConsole.Desktop;

public sealed class RecordingPlaybackStateChangedEventArgs(
    RecordingId recordingId,
    string path,
    bool isPlaying) : EventArgs
{
    public RecordingId RecordingId { get; } = recordingId;
    public string Path { get; } = path;
    public bool IsPlaying { get; } = isPlaying;
}

/// <summary>
/// Desktop compatibility facade for recording playback. Application owns the
/// playback lifecycle by stable ID; this adapter retains path-based calls for
/// legacy catalog entries and older desktop callers.
/// </summary>
public sealed class RecordingPlaybackCoordinator : IAsyncDisposable
{
    private readonly RecordingPlaybackStoreAdapter store;
    private readonly ApplicationPlaybackCoordinator inner;
    private readonly Action<Exception>? faultHandler;

    public RecordingPlaybackCoordinator(
        IRecordingStore recordingStore,
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        Action<Exception>? faultHandler = null,
        Action<RecordingPlaybackStartupMetrics>? startupObserver = null)
    {
        this.faultHandler = faultHandler;
        store = new RecordingPlaybackStoreAdapter(
            recordingStore ?? throw new ArgumentNullException(nameof(recordingStore)));
        inner = CreateInner(createAudioBackend, getOutputDeviceId, faultHandler, startupObserver);
    }

    public RecordingPlaybackCoordinator(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        Action<Exception>? faultHandler = null,
        Action<RecordingPlaybackStartupMetrics>? startupObserver = null)
    {
        this.faultHandler = faultHandler;
        store = new RecordingPlaybackStoreAdapter();
        inner = CreateInner(createAudioBackend, getOutputDeviceId, faultHandler, startupObserver);
    }

    public event EventHandler<RecordingPlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public bool IsPlaying(string? path = null)
        => path is null
            ? inner.IsPlaying()
            : store.TryGetId(path, out RecordingId id) && inner.IsPlaying(id);

    public bool IsPlaying(RecordingId recordingId) => inner.IsPlaying(recordingId);

    public Task StartAsync(string path, CancellationToken cancellationToken = default)
    {
        RecordingId recordingId = store.RegisterPath(path);
        return inner.StartAsync(recordingId, cancellationToken);
    }

    public Task StartAsync(
        RecordingId recordingId,
        string? legacyPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(legacyPath))
            store.RegisterPath(legacyPath, recordingId);
        return inner.StartAsync(recordingId, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
        => inner.StopAsync(cancellationToken);

    public Task ResetAudioBackendAsync(CancellationToken cancellationToken = default)
        => inner.ResetAudioBackendAsync(cancellationToken);

    public Task<bool> StopIfPlayingAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return store.TryGetId(path, out RecordingId id)
            ? inner.StopIfPlayingAsync(id, cancellationToken)
            : Task.FromResult(false);
    }

    public Task<bool> StopIfPlayingAsync(
        RecordingId recordingId,
        CancellationToken cancellationToken = default)
        => inner.StopIfPlayingAsync(recordingId, cancellationToken);

    public ValueTask DisposeAsync()
    {
        inner.PlaybackStateChanged -= HandlePlaybackStateChanged;
        return inner.DisposeAsync();
    }

    private ApplicationPlaybackCoordinator CreateInner(
        Func<IAudioBackend> createAudioBackend,
        Func<string?> getOutputDeviceId,
        Action<Exception>? faultHandler,
        Action<RecordingPlaybackStartupMetrics>? startupObserver)
    {
        var coordinator = new ApplicationPlaybackCoordinator(
            store,
            createAudioBackend,
            getOutputDeviceId,
            faultHandler,
            startupObserver);
        coordinator.PlaybackStateChanged += HandlePlaybackStateChanged;
        return coordinator;
    }

    private void HandlePlaybackStateChanged(
        object? sender,
        ApplicationPlaybackStateChangedEventArgs e)
    {
        string path = store.TryGetPath(e.RecordingId, out string resolvedPath)
            ? resolvedPath
            : string.Empty;
        EventHandler<RecordingPlaybackStateChangedEventArgs>? handlers = PlaybackStateChanged;
        if (handlers is null)
            return;

        var eventArgs = new RecordingPlaybackStateChangedEventArgs(
            e.RecordingId,
            path,
            e.IsPlaying);
        foreach (EventHandler<RecordingPlaybackStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
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
        }
    }

    private sealed class RecordingPlaybackStoreAdapter : IRecordingStore
    {
        private readonly object sync = new();
        private readonly IRecordingStore? inner;
        private readonly Dictionary<RecordingId, string> pathsById = [];
        private readonly Dictionary<string, RecordingId> idsByPath =
            new(FileSystemPathIdentity.Comparer);

        public RecordingPlaybackStoreAdapter(IRecordingStore? inner = null)
        {
            this.inner = inner;
        }

        public RecordingId RegisterPath(string path, RecordingId? recordingId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("The recording file was not found.", fullPath);

            lock (sync)
            {
                if (recordingId is null && idsByPath.TryGetValue(fullPath, out RecordingId existing))
                    return existing;

                RecordingId id = recordingId ?? RecordingId.New();
                pathsById[id] = fullPath;
                idsByPath[fullPath] = id;
                return id;
            }
        }

        public bool TryGetId(string path, out RecordingId recordingId)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                lock (sync)
                    return idsByPath.TryGetValue(fullPath, out recordingId);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                recordingId = default;
                return false;
            }
        }

        public bool TryGetPath(RecordingId recordingId, out string path)
        {
            lock (sync)
                return pathsById.TryGetValue(recordingId, out path!);
        }

        public ValueTask<IRecordingWriteHandle> CreateAsync(
            CallId callId,
            ChannelId channelId,
            DateTimeOffset startedAt,
            string mediaType,
            CancellationToken cancellationToken = default)
            => inner?.CreateAsync(callId, channelId, startedAt, mediaType, cancellationToken)
               ?? ValueTask.FromException<IRecordingWriteHandle>(
                   new NotSupportedException("The legacy playback adapter is read-only."));

        public async ValueTask<Stream> OpenReadAsync(
            RecordingId id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetPath(id, out string registeredPath))
                return OpenPath(registeredPath);

            if (inner is not null)
                return await inner.OpenReadAsync(id, cancellationToken).ConfigureAwait(false);

            throw new KeyNotFoundException($"Recording '{id}' has no desktop path fallback.");
        }

        private static FileStream OpenPath(string path)
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous);
        }

        public async IAsyncEnumerable<RecordingDescriptor> ListAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (inner is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }

            await foreach (RecordingDescriptor descriptor in inner
                               .ListAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return descriptor;
            }
        }
    }
}
