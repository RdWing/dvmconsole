// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed class ReceiveSessionPort(
    ChannelReceiveAudioCoordinator audio,
    ChannelReceiveWorkQueue work,
    ReceiveMuteState mute,
    SemaphoreSlim gate,
    Func<IEnumerable<ConsoleChannelState>> getChannels,
    Func<bool> isDisposing,
    Action<ChannelId> resetTiming,
    Action<string> publishStatus,
    IClock? clock = null) : IReceiveSessionPort
{
    private readonly IClock clock = clock ?? SystemClock.Instance;
    private readonly IReadOnlyDictionary<ChannelId, ConsoleChannelState> channelsById = getChannels()
        .Distinct()
        .ToDictionary(channel => channel.Id);

    public bool IsDisposing => isDisposing();
    public DateTimeOffset UtcNow => clock.UtcNow;
    public IReadOnlyList<ChannelId> LivePlaybackChannels => audio.LivePlaybackChannels;
    public IEnumerable<ReceiveSessionChannelState> Channels => channelsById.Values
        .Select(channel =>
        {
            ChannelOperatorSnapshot state = channel.Operator.Snapshot;
            return new ReceiveSessionChannelState(channel.Id,
                state.AudioEnabled, state.RecordingEnabled, state.AudioSuspended);
        });

    public bool IsActive(ChannelId channelId) => audio.IsActive(channelId);

    public bool ShouldEnableLivePlayback(ChannelId channelId, bool isTemporarilySuspended)
        => mute.ShouldEnableLivePlayback(channelId, Resolve(channelId).Operator.Snapshot.AudioEnabled, isTemporarilySuspended);

    public Task StartAsync(ChannelId channelId, CancellationToken cancellationToken)
        => audio.StartAsync(Describe(channelId), cancellationToken);

    public Task StopAsync(ChannelId channelId, CancellationToken cancellationToken)
        => audio.StopAsync(channelId, cancellationToken);

    public Task EnsureDecodeAsync(ChannelId channelId, CancellationToken cancellationToken)
        => audio.EnsureDecodeAsync(Describe(channelId), cancellationToken: cancellationToken);

    public Task SetLivePlaybackEnabledAsync(
        ChannelId channelId,
        bool enabled,
        CancellationToken cancellationToken)
        => audio.SetLivePlaybackEnabledAsync(channelId, enabled, cancellationToken);

    public void StartWork(ChannelId channelId) => work.Start(channelId);
    public void ResetTiming(ChannelId channelId) => resetTiming(channelId);
    public void PublishStatus(string text) => publishStatus(text);

    public async Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private ReceiveChannelDescriptor Describe(ChannelId channelId)
        => new(channelId, Resolve(channelId).Runtime.Definition);

    private ConsoleChannelState Resolve(ChannelId channelId)
        => channelsById[channelId];
}
