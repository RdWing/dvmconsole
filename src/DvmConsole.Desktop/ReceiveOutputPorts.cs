// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

internal interface IReceiveOutputRoutePort
{
    IReadOnlyList<ChannelId> LivePlaybackChannels { get; }
    bool IsActive(ChannelId channelId);
    object? GetSessionIdentity(ChannelId channelId);
    Task StartAsync(ReceiveChannelDescriptor channel, CancellationToken cancellationToken);
    Task StopAsync(ChannelId channelId, CancellationToken cancellationToken);
    Task SetLivePlaybackEnabledAsync(
        ChannelId channelId,
        bool enabled,
        CancellationToken cancellationToken);
    void StartWork(ChannelId channelId);
    Task StopWorkAsync(ChannelId channelId);
    void ResetDiagnostics(ChannelId channelId);
    Task<ReceiveRouteRecoveryResult> RecoverSelectedAsync(
        IReadOnlyCollection<ChannelId> channelIds,
        CancellationToken cancellationToken);
    Task<T> RunExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
    void RecordRestarted(ChannelId channelId);
    void RecordFailure(ChannelId channelId, DateTimeOffset retryAt);
    Task ReconcileAsync(CancellationToken cancellationToken);
    Task ApplyPlaybackPolicyAsync(
        IReadOnlyList<(ChannelId ChannelId, bool Enabled)> changes,
        CancellationToken cancellationToken);
}

internal interface IReceiveOutputMutePort
{
    bool IsMuted(ChannelViewModel channel);
    bool ShouldEnableLivePlayback(ChannelViewModel channel, bool isTemporarilySuspended);
    string? GetEffectiveReason(ChannelViewModel channel, bool outputMuted);
    bool Toggle(SystemViewModel system);
    bool Toggle(ZoneViewModel zone);
}

internal interface IReceiveOutputPresentationPort
{
    ChannelViewModel Resolve(ChannelId channelId);
    ChannelViewModel[] Resolve(IEnumerable<ChannelId> channelIds);
    Task RunAsync(Action action);
    void SetSelectionPreference(ChannelViewModel channel, bool enabled);
    void StopRecording(ChannelViewModel channel);
    void NotifyMuteChanged();
    void PublishStatus(string text);
}

internal interface IReceiveOutputLifetimePort
{
    bool IsDisposing { get; }
    void ObserveRecovery(TimeSpan elapsed, string result);
}

internal sealed class ReceiveOutputRoutePort(
    ChannelReceiveAudioCoordinator audio,
    ChannelReceiveWorkQueue workQueue,
    ReceiveSessionController sessions,
    SemaphoreSlim reconfigurationGate,
    ReceivePipelineTimingReporter pipelineTiming,
    ReceiveJitterEventReporter jitterEvents,
    Func<ChannelId, ChannelViewModel> resolveChannel) : IReceiveOutputRoutePort
{
    public IReadOnlyList<ChannelId> LivePlaybackChannels => audio.LivePlaybackChannels;

    public bool IsActive(ChannelId channelId) => audio.IsActive(channelId);

    public object? GetSessionIdentity(ChannelId channelId) => audio.GetSessionIdentity(channelId);

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

    public void ResetDiagnostics(ChannelId channelId)
    {
        ChannelViewModel channel = resolveChannel(channelId);
        pipelineTiming.Reset(channel);
        jitterEvents.Reset(channel);
    }

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
                        $"Unable to apply receive-output policy to {resolveChannel(channelId).Name}.",
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

internal sealed class ReceiveOutputMutePort(ReceiveOutputMutePolicy policy) : IReceiveOutputMutePort
{
    public bool IsMuted(ChannelViewModel channel) => policy.IsMuted(channel);

    public bool ShouldEnableLivePlayback(ChannelViewModel channel, bool isTemporarilySuspended)
        => policy.ShouldEnableLivePlayback(channel, isTemporarilySuspended);

    public string? GetEffectiveReason(ChannelViewModel channel, bool outputMuted)
        => policy.GetEffectiveReason(channel, outputMuted);

    public bool Toggle(SystemViewModel system) => policy.Toggle(system);

    public bool Toggle(ZoneViewModel zone) => policy.Toggle(zone);
}

internal sealed class ReceiveOutputPresentationPort(
    UserSettings settings,
    CallRecordingManager recordings,
    Func<ChannelId, ChannelViewModel> resolveChannel,
    Func<IEnumerable<ChannelId>, ChannelViewModel[]> resolveChannels,
    Func<Action, Task> runOnUiThread,
    Action persistSettings,
    Action notifyMutePresentation,
    Action<string> publishStatus) : IReceiveOutputPresentationPort
{
    public ChannelViewModel Resolve(ChannelId channelId) => resolveChannel(channelId);

    public ChannelViewModel[] Resolve(IEnumerable<ChannelId> channelIds)
        => resolveChannels(channelIds);

    public Task RunAsync(Action action) => runOnUiThread(action);

    public void SetSelectionPreference(ChannelViewModel channel, bool enabled)
    {
        HashSet<string> selected = settings.ReceiveEnabledChannelKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = enabled
            ? selected.Add(channel.SettingsKey)
            : selected.Remove(channel.SettingsKey);
        if (!changed)
            return;

        settings.ReceiveEnabledChannelKeys = selected
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        persistSettings();
    }

    public void StopRecording(ChannelViewModel channel) => recordings.StopChannel(channel);

    public void NotifyMuteChanged() => notifyMutePresentation();

    public void PublishStatus(string text) => publishStatus(text);
}

internal sealed class ReceiveOutputLifetimePort(
    Func<bool> isDisposing,
    Action<TimeSpan, string> observeRecovery) : IReceiveOutputLifetimePort
{
    public bool IsDisposing => isDisposing();

    public void ObserveRecovery(TimeSpan elapsed, string result)
        => observeRecovery(elapsed, result);
}
