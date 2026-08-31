namespace DvmConsole.Desktop;

using DvmConsole.Application;

public sealed record RecordingFinalizationResult(
    CallRecordingMetadata? Metadata,
    uint StreamId,
    string? Diagnostic,
    Exception? Error)
{
    public bool IsPlayable => Metadata?.IsPlayable == true;
    internal ChannelId? ChannelId { get; init; }
    internal RecordingFinalizationDescriptor? Descriptor { get; init; }
}

internal sealed record RecordingFinalizationJob(
    uint StreamId,
    Func<CancellationToken, Task<RecordingFinalizationResult>> ExecuteAsync,
    int MaximumAttempts = 3,
    TimeSpan? RetryDelay = null)
{
    public TimeSpan EffectiveRetryDelay => RetryDelay ?? TimeSpan.FromMilliseconds(250);
}
