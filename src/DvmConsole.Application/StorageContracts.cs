namespace DvmConsole.Application;

public sealed record AssetDescriptor(
    AssetId Id,
    string DisplayName,
    string MediaType,
    long Length);

public interface IAssetStore
{
    ValueTask<AssetDescriptor> ImportAsync(
        string displayName,
        string mediaType,
        Stream content,
        CancellationToken cancellationToken = default);
    ValueTask<Stream> OpenReadAsync(
        AssetId id,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<AssetDescriptor> ListAsync(
        CancellationToken cancellationToken = default);
}

public sealed record RecordingDescriptor(
    RecordingId Id,
    CallId CallId,
    ChannelId ChannelId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string MediaType,
    long Length,
    bool IsFinalized,
    string? Fault);

/// <summary>
/// Immutable channel facts captured at a recording operation boundary. The
/// recording service never needs a presentation object to identify a channel
/// or decide how its media should be described.
/// </summary>
public sealed record ChannelRecordingDescriptor(
    ChannelId Id,
    DvmConsole.Core.Runtime.ChannelRuntimeDefinition Definition,
    bool RecordingEnabled,
    bool TransmitEncrypted,
    uint? ActiveStreamId = null,
    uint? ActiveSourceId = null);

public readonly record struct RecordingEncryptionDescriptor(
    bool IsKnown,
    bool IsSecure,
    byte? AlgorithmId,
    ushort? KeyId)
{
    public static RecordingEncryptionDescriptor Unknown => default;
    public static RecordingEncryptionDescriptor Clear { get; } = new(
        IsKnown: true,
        IsSecure: false,
        AlgorithmId: null,
        KeyId: null);

    public static RecordingEncryptionDescriptor Secure(byte? algorithmId, ushort? keyId)
        => new(
            IsKnown: true,
            IsSecure: true,
            algorithmId,
            keyId);
}

public sealed record RecordingCaptureContext(
    DvmConsole.Core.Runtime.ChannelRuntimeDefinition Channel,
    string Direction,
    string RecordingSourceType,
    uint StreamId,
    IReadOnlyList<uint> StreamIds,
    uint? SourceId,
    string SubscriberAlias,
    RecordingEncryptionDescriptor Encryption,
    int? RetentionDays,
    long? ReceiveEpisodeId,
    DateTimeOffset ObservedAt);

public interface IRecordingWriteHandle : IAsyncDisposable
{
    RecordingId Id { get; }
    Stream Stream { get; }
    void UpdateContext(RecordingCaptureContext context)
    {
    }
    ValueTask CommitAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default);
    ValueTask AbortAsync(
        string? fault,
        CancellationToken cancellationToken = default);
}

public interface IRecordingStore
{
    ValueTask<IRecordingWriteHandle> CreateAsync(
        CallId callId,
        ChannelId channelId,
        DateTimeOffset startedAt,
        string mediaType,
        CancellationToken cancellationToken = default);
    ValueTask<Stream> OpenReadAsync(
        RecordingId id,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<RecordingDescriptor> ListAsync(
        CancellationToken cancellationToken = default);
}

public interface IReadableDocument
{
    string DisplayName { get; }
    string? OriginIdentity { get; }
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

public interface IWritableDocument : IReadableDocument
{
    ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default);
}

public interface IImportDocumentSet
{
    IReadableDocument Primary { get; }
    ValueTask<IReadableDocument?> ResolveCompanionAsync(
        string relativeReference,
        CancellationToken cancellationToken = default);
}

public interface IExportDocumentSet
{
    IWritableDocument Primary { get; }
    ValueTask<IWritableDocument> CreateCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default);
    ValueTask<IReadableDocument?> ResolveExportedCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default);
}
