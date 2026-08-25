namespace DvmConsole.Desktop;

public sealed record RecordingFinalizationResult(
    CallRecordingMetadata? Metadata,
    uint StreamId,
    string? Diagnostic,
    Exception? Error)
{
    public bool IsPlayable => Metadata?.IsPlayable == true;
    internal ChannelViewModel? Channel { get; init; }
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
