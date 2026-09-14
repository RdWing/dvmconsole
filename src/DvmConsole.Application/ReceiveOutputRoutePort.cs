// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed class ReceiveOutputRoutePort(
    ChannelReceiveAudioCoordinator audio,
    ChannelReceiveWorkQueue workQueue,
    ReceiveSessionController sessions,
    SemaphoreSlim reconfigurationGate,
    Action<ChannelId> resetDiagnostics,
    Func<ChannelId, string> getChannelName) : IReceiveOutputRoutePort
{
    public IReadOnlyList<ChannelId> LivePlaybackChannels => audio.LivePlaybackChannels;

    public object? GetSessionIdentity(ChannelId channelId) => audio.GetSessionIdentity(channelId);

    public bool IsActive(ChannelId channelId) => audio.IsActive(channelId);

    public Task StartAsync(ReceiveChannelDescriptor channel, CancellationToken cancellationToken)
        => audio.StartAsync(channel, cancellationToken);

    public Task StopAsync(ChannelId channelId, CancellationToken cancellationToken)
        => audio.StopAsync(channelId, cancellationToken);

    public Task SetLivePlaybackEnabledAsync(
        ChannelId channelId,
        bool enabled,
        CancellationToken cancellationToken)
        => audio.SetLivePlaybackEnabledAsync(channelId, enabled, cancellationToken);

    public void StartWork(ChannelId channelId) => workQueue.Start(channelId);

    public Task StopWorkAsync(ChannelId channelId) => workQueue.StopAsync(channelId);

    public void ResetDiagnostics(ChannelId channelId) => resetDiagnostics(channelId);

    public Task<ReceiveRouteRecoveryResult> RecoverSelectedAsync(
        IReadOnlyCollection<ChannelId> channelIds,
        CancellationToken cancellationToken)
        => audio.RecoverSelectedAsync(channelIds, cancellationToken);

    public async Task<T> RunExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await reconfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            reconfigurationGate.Release();
        }
    }

    public void RecordRestarted(ChannelId channelId)
        => sessions.RecordRestarted(channelId);

    public void RecordFailure(ChannelId channelId, DateTimeOffset retryAt)
        => sessions.RecordFailure(channelId, retryAt);

    public Task ReconcileAsync(CancellationToken cancellationToken)
        => sessions.ReconcileAsync(cancellationToken);

    public async Task ApplyPlaybackPolicyAsync(
        IReadOnlyList<(ChannelId ChannelId, bool Enabled)> changes,
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        await reconfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach ((ChannelId channelId, bool enabled) in changes)
            {
                if (!audio.IsActive(channelId))
                    continue;
                try
                {
                    await audio
                        .SetLivePlaybackEnabledAsync(channelId, enabled, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(new InvalidOperationException(
                        $"Unable to apply receive-output policy to {getChannelName(channelId)}.",
                        exception));
                }
            }
        }
        finally
        {
            reconfigurationGate.Release();
        }

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Receive-output policy application failed.", failures);
    }
}
