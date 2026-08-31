using DvmConsole.Audio;
using DvmConsole.Ptt;
using DvmConsole.Vocoder;
using System.Diagnostics;

namespace DvmConsole.Application;

public sealed record RadioSystemDescriptor(
    SystemId Id,
    string Name,
    string Protocol,
    IReadOnlyDictionary<string, string> ConnectionParameters);

public sealed record RadioTrafficRecord(
    SystemId SystemId,
    IReadOnlyList<ChannelId> CandidateChannels,
    DvmConsole.Core.Runtime.IRadioMediaFrame Traffic,
    DateTimeOffset ReceivedAt,
    long BoundaryTimestamp = 0,
    long TransportIngressTimestamp = 0);

public sealed record TalkgroupAuthorityChannelRecord(
    ChannelId ChannelId,
    TargetAuthorityState State,
    string? Reason);

public sealed record TalkgroupAuthorityRecord(
    SystemId SystemId,
    IReadOnlyList<TalkgroupAuthorityChannelRecord> Channels,
    DateTimeOffset ObservedAt);

public interface IRadioSession : IRadioTrafficEndpoint, IAsyncDisposable
{
    SystemId SystemId { get; }
    bool IsConnectionActive { get; }
    event EventHandler<RadioTrafficRecord>? TrafficReceived;
    event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged;
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask QuiesceAsync(CancellationToken cancellationToken = default);
}

public interface IRadioSessionFactory
{
    ValueTask<IRadioSession> CreateAsync(
        RadioSystemDescriptor system,
        CancellationToken cancellationToken = default);
}

public enum MicrophonePermissionState
{
    Unknown,
    Granted,
    Requested,
    Denied,
    Restricted,
    Unavailable
}

public interface IMicrophonePermissionService
{
    ValueTask<MicrophonePermissionState> GetStateAsync(
        CancellationToken cancellationToken = default);
    ValueTask<MicrophonePermissionState> RequestAsync(
        CancellationToken cancellationToken = default);
}

public interface IApplicationLifecycle
{
    bool IsActive { get; }
    event EventHandler? Activated;
    event EventHandler? Deactivated;
    event EventHandler? Suspending;
    event EventHandler? Resumed;
    event EventHandler? Stopping;
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();
    private SystemClock()
    {
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IScheduledWork : IAsyncDisposable
{
    bool IsRunning { get; }
    void Start();
    void Stop();
}

public interface IApplicationScheduler
{
    IScheduledWork CreatePeriodic(
        TimeSpan interval,
        Func<CancellationToken, ValueTask> callback,
        bool startImmediately = true);
}

public interface IApplicationDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class SystemApplicationDelay : IApplicationDelay
{
    public static SystemApplicationDelay Instance { get; } = new();

    private SystemApplicationDelay()
    {
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);
}

internal interface IMonotonicTimeSource
{
    long TimestampFrequency { get; }
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp);
}

internal interface IReceiveWorkQueueScheduler : IMonotonicTimeSource
{
    ValueTask<bool> WaitAsync(CoalescingWakeSignal signal, TimeSpan timeout);
}

internal sealed class SystemReceiveWorkQueueScheduler : IReceiveWorkQueueScheduler
{
    public static SystemReceiveWorkQueueScheduler Instance { get; } = new();

    private SystemReceiveWorkQueueScheduler()
    {
    }

    public long GetTimestamp()
        => Stopwatch.GetTimestamp();

    public long TimestampFrequency => Stopwatch.Frequency;

    public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp, endTimestamp);

    public ValueTask<bool> WaitAsync(CoalescingWakeSignal signal, TimeSpan timeout)
        => signal.WaitAsync(timeout);
}

public sealed record ConsoleHostServices(
    IRadioSessionFactory RadioSessions,
    IAudioBackendFactory AudioBackends,
    IVocoderFactory Vocoders,
    IConfigurationLibrary Configurations,
    IAssetStore Assets,
    IRecordingStore Recordings,
    IApplicationLifecycle Lifecycle,
    IClock Clock,
    IApplicationScheduler Scheduler,
    IApplicationDelay Delay,
    IMicrophonePermissionService MicrophonePermission,
    IReadOnlyList<IPttInputSourceFactory> PttInputSources);
