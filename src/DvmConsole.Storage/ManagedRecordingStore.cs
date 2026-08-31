using System.Text.Json;
using DvmConsole.Application;

namespace DvmConsole.Storage;

public sealed class ManagedRecordingStore : IRecordingStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string root;
    private readonly string catalogPath;

    public ManagedRecordingStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        root = Path.GetFullPath(rootPath);
        catalogPath = Path.Combine(root, "catalog.json");
        Directory.CreateDirectory(Path.Combine(root, "active"));
        Directory.CreateDirectory(Path.Combine(root, "content"));
        if (!File.Exists(catalogPath))
            WriteCatalog([]);
    }

    public async ValueTask<IRecordingWriteHandle> CreateAsync(
        CallId callId,
        ChannelId channelId,
        DateTimeOffset startedAt,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecordingId id = RecordingId.New();
            string activePath = ActivePath(id);
            var stream = new FileStream(
                activePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous);
            return new WriteHandle(this, id, callId, channelId, startedAt, mediaType.Trim(), activePath, stream);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<Stream> OpenReadAsync(
        RecordingId id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecordingDescriptor descriptor = ReadCatalog().FirstOrDefault(recording => recording.Id == id)
                ?? throw new KeyNotFoundException($"Recording '{id.Value:N}' is not in the managed store.");
            if (!descriptor.IsFinalized)
                throw new InvalidOperationException("The recording is not finalized.");
            return new FileStream(
                ContentPath(id),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous);
        }
        finally
        {
            gate.Release();
        }
    }

    public async IAsyncEnumerable<RecordingDescriptor> ListAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RecordingDescriptor[] snapshot;
        try
        {
            snapshot = ReadCatalog().OrderByDescending(recording => recording.StartedAt).ToArray();
        }
        finally
        {
            gate.Release();
        }
        foreach (RecordingDescriptor descriptor in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return descriptor;
        }
    }

    private async ValueTask CompleteAsync(
        WriteHandle handle,
        TimeSpan duration,
        string? fault,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await handle.CloseStreamAsync(cancellationToken).ConfigureAwait(false);
            string finalPath = ContentPath(handle.Id);
            File.Move(handle.ActivePath, finalPath);
            var descriptor = new RecordingDescriptor(
                handle.Id,
                handle.CallId,
                handle.ChannelId,
                handle.StartedAt,
                duration,
                handle.MediaType,
                new FileInfo(finalPath).Length,
                IsFinalized: fault is null,
                Fault: fault);
            List<RecordingDescriptor> catalog = ReadCatalog();
            catalog.Add(descriptor);
            WriteCatalog(catalog);
        }
        finally
        {
            gate.Release();
        }
    }

    private string ActivePath(RecordingId id) => Path.Combine(root, "active", id.Value.ToString("N") + ".part");
    private string ContentPath(RecordingId id) => Path.Combine(root, "content", id.Value.ToString("N") + ".media");

    private List<RecordingDescriptor> ReadCatalog()
        => JsonSerializer.Deserialize(File.ReadAllText(catalogPath), StorageJsonContext.Default.ListRecordingDescriptor) ?? [];

    private void WriteCatalog(List<RecordingDescriptor> catalog)
        => AtomicJsonFile.Write(catalogPath, catalog, StorageJsonContext.Default.ListRecordingDescriptor);

    private sealed class WriteHandle(
        ManagedRecordingStore owner,
        RecordingId id,
        CallId callId,
        ChannelId channelId,
        DateTimeOffset startedAt,
        string mediaType,
        string activePath,
        FileStream stream) : IRecordingWriteHandle
    {
        private int completed;
        public RecordingId Id { get; } = id;
        public CallId CallId { get; } = callId;
        public ChannelId ChannelId { get; } = channelId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string MediaType { get; } = mediaType;
        public string ActivePath { get; } = activePath;
        public Stream Stream => stream;

        public ValueTask CommitAsync(TimeSpan duration, CancellationToken cancellationToken = default)
            => CompleteOnceAsync(duration, null, cancellationToken);

        public ValueTask AbortAsync(string? fault, CancellationToken cancellationToken = default)
            => CompleteOnceAsync(TimeSpan.Zero, fault ?? "Recording aborted.", cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) == 0)
                await owner.CompleteAsync(this, TimeSpan.Zero, "Recording handle disposed before commit.", CancellationToken.None).ConfigureAwait(false);
            else
                await stream.DisposeAsync().ConfigureAwait(false);
        }

        internal async ValueTask CloseStreamAsync(CancellationToken cancellationToken)
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        private async ValueTask CompleteOnceAsync(
            TimeSpan duration,
            string? fault,
            CancellationToken cancellationToken)
        {
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
                return;
            await owner.CompleteAsync(this, duration, fault, cancellationToken).ConfigureAwait(false);
        }
    }
}
