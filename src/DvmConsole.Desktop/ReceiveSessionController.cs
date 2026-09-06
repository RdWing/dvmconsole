// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using System.Collections.Concurrent;

namespace DvmConsole.Desktop;

internal readonly record struct ReceiveSessionChannelState(
    ChannelId Id,
    bool AudioEnabled,
    bool RecordingEnabled,
    bool AudioSuspended);

internal interface IReceiveSessionPort
{
    bool IsDisposing { get; }
    DateTimeOffset UtcNow { get; }
    IReadOnlyList<ChannelId> LivePlaybackChannels { get; }
    IEnumerable<ReceiveSessionChannelState> Channels { get; }
    bool IsActive(ChannelId channelId);
    bool ShouldEnableLivePlayback(ChannelId channelId, bool isTemporarilySuspended);
    Task StartAsync(ChannelId channelId, CancellationToken cancellationToken);
    Task StopAsync(ChannelId channelId, CancellationToken cancellationToken);
    Task EnsureDecodeAsync(ChannelId channelId, CancellationToken cancellationToken);
    Task SetLivePlaybackEnabledAsync(
        ChannelId channelId,
        bool enabled,
        CancellationToken cancellationToken);
    void StartWork(ChannelId channelId);
    void ResetTiming(ChannelId channelId);
    void PublishStatus(string text);
    Task RunExclusiveAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken);
}

internal sealed class ReceiveSessionPort(
    ChannelReceiveAudioCoordinator audio,
    ChannelReceiveWorkQueue work,
    ReceiveOutputMutePolicy mute,
    SemaphoreSlim gate,
    Func<IEnumerable<ChannelViewModel>> getChannels,
    Func<bool> isDisposing,
    Action<ChannelViewModel> resetTiming,
    Action<string> publishStatus,
    Func<DateTimeOffset>? getUtcNow = null) : IReceiveSessionPort
{
    private readonly Func<DateTimeOffset> clock = getUtcNow ??
        (static () => DateTimeOffset.UtcNow);
    private readonly IReadOnlyDictionary<ChannelId, ChannelViewModel> channelsById = getChannels()
        .Distinct()
        .ToDictionary(channel => channel.Id);

    public bool IsDisposing => isDisposing();
    public DateTimeOffset UtcNow => clock();
    public IReadOnlyList<ChannelId> LivePlaybackChannels => audio.LivePlaybackChannels;
    public IEnumerable<ReceiveSessionChannelState> Channels => channelsById.Values
        .Select(channel => new ReceiveSessionChannelState(
            channel.Id,
            channel.IsAudioEnabled,
            channel.IsRecordingEnabled,
            channel.IsAudioSuspended));

    public bool IsActive(ChannelId channelId) => audio.IsActive(channelId);

    public bool ShouldEnableLivePlayback(ChannelId channelId, bool isTemporarilySuspended)
        => mute.ShouldEnableLivePlayback(Resolve(channelId), isTemporarilySuspended);

    public Task StartAsync(ChannelId channelId, CancellationToken cancellationToken)
        => audio.StartAsync(Resolve(channelId), cancellationToken);

    public Task StopAsync(ChannelId channelId, CancellationToken cancellationToken)
        => audio.StopAsync(channelId, cancellationToken);

    public Task EnsureDecodeAsync(ChannelId channelId, CancellationToken cancellationToken)
        => audio.EnsureDecodeAsync(Resolve(channelId), cancellationToken: cancellationToken);

    public Task SetLivePlaybackEnabledAsync(
        ChannelId channelId,
        bool enabled,
        CancellationToken cancellationToken)
        => audio.SetLivePlaybackEnabledAsync(channelId, enabled, cancellationToken);

    public void StartWork(ChannelId channelId) => work.Start(channelId);
    public void ResetTiming(ChannelId channelId) => resetTiming(Resolve(channelId));
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

    private ChannelViewModel Resolve(ChannelId channelId)
        => channelsById[channelId];
}

// Owns receive-session liveness, retry throttling, and restart sequencing.
// The MainWindowViewModel remains the XAML facade while runtime ownership stays
// behind this narrow coordinator.
internal sealed class ReceiveSessionController
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly IReceiveSessionPort port;
    private readonly ConcurrentDictionary<ChannelId, DateTimeOffset> retryAfter = new();

    public ReceiveSessionController(IReceiveSessionPort port)
        => this.port = port ?? throw new ArgumentNullException(nameof(port));

    public void RecordRestarted(ChannelId channelId)
        => retryAfter.TryRemove(channelId, out _);

    public void RecordFailure(ChannelId channelId, DateTimeOffset? retryAt = null)
        => retryAfter[channelId] = retryAt ?? port.UtcNow.Add(RetryDelay);

    public bool IsRetryPending(ChannelId channelId)
        => retryAfter.TryGetValue(channelId, out DateTimeOffset retryAt) && retryAt > port.UtcNow;

    public async Task RetireFailedSessionAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        if (port.IsDisposing)
            return;
        // The receive worker calls this after releasing its processing lease.
        // Reconfiguration holds the outer gate while draining that worker, so
        // retirement must use only the audio coordinator's own lifetime lock.
        // StopAsync does not join the receive worker or change operator intent.
        RecordFailure(channelId);
        await port.StopAsync(channelId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (port.IsDisposing)
            return;

        await port.RunExclusiveAsync(async operationCancellationToken =>
        {
            if (port.IsDisposing)
                return;

            DateTimeOffset now = port.UtcNow;
            HashSet<ChannelId> livePlaybackChannels = port.LivePlaybackChannels.ToHashSet();
            // Freeze pending restarts before the first await without copying every channel.
            ReceiveSessionChannelState[] missing = port.Channels
                .Where(channel => (channel.AudioEnabled || channel.RecordingEnabled) &&
                    (!port.IsActive(channel.Id) ||
                     (port.ShouldEnableLivePlayback(
                          channel.Id,
                          isTemporarilySuspended: channel.AudioSuspended) &&
                      !livePlaybackChannels.Contains(channel.Id))) &&
                    (!retryAfter.TryGetValue(channel.Id, out DateTimeOffset retryAt) || retryAt <= now))
                .ToArray();
            if (missing.Length == 0)
                return;

            int restarted = 0;
            foreach (ReceiveSessionChannelState channel in missing)
            {
                operationCancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (channel.AudioEnabled)
                    {
                        await port.StartAsync(channel.Id, operationCancellationToken).ConfigureAwait(false);
                        if (!port.ShouldEnableLivePlayback(
                                channel.Id,
                                isTemporarilySuspended: channel.AudioSuspended))
                        {
                            await port.SetLivePlaybackEnabledAsync(
                                    channel.Id,
                                    enabled: false,
                                    operationCancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await port.EnsureDecodeAsync(channel.Id, operationCancellationToken)
                            .ConfigureAwait(false);
                    }

                    port.StartWork(channel.Id);
                    port.ResetTiming(channel.Id);
                    RecordRestarted(channel.Id);
                    restarted++;
                }
                catch when (!operationCancellationToken.IsCancellationRequested)
                {
                    RecordFailure(channel.Id, now.Add(RetryDelay));
                }
            }

            port.PublishStatus(restarted == missing.Length
                ? $"Restored {restarted} receive decode session(s)."
                : $"RX decode unavailable; retrying {missing.Length - restarted} session(s).");
        }, cancellationToken).ConfigureAwait(false);
    }
}
