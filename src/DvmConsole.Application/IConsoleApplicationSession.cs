namespace DvmConsole.Application;

/// <summary>
/// Narrow host adapter used while platform-specific radio and UI objects are
/// composed outside the portable runtime. Only immutable, ID-keyed state and
/// commands cross this boundary; Application owns session revisioning,
/// telemetry separation, quiescence, and disposal.
/// </summary>
public interface IConsoleSessionRuntimeAdapter : IAsyncDisposable
{
    ConsoleTopologySnapshot CaptureTopology();
    ConsoleRuntimeSnapshot CaptureSnapshot();
    IReadOnlyList<ConsoleCallHistoryRecord> History { get; }
    IConsoleCommands Commands { get; }

    event EventHandler? ControlStateInvalidated;
    event EventHandler<ChannelMeterSample>? MeterSampled;
    event EventHandler<ConsoleLogEvent>? LogPublished;

    ValueTask QuiesceAsync(CancellationToken cancellationToken);
    ValueTask FlushSettingsAsync(CancellationToken cancellationToken);
}

public interface IConsoleApplicationSession : IAsyncDisposable
{
    ConsoleSessionId Id { get; }
    ConsoleTopologySnapshot Topology { get; }
    ConsoleRuntimeSnapshot Snapshot { get; }
    IReadOnlyList<ConsoleCallHistoryRecord> History => [];
    IConsoleCommands Commands { get; }

    event EventHandler<ConsoleSnapshotChangedEventArgs>? SnapshotChanged;
    event EventHandler<ChannelMeterSample>? MeterSampled;
    event EventHandler<ConsoleLogEvent>? LogPublished;

    ValueTask QuiesceAsync(CancellationToken cancellationToken);
    ValueTask FlushSettingsAsync(CancellationToken cancellationToken);
}
