// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Threading;

namespace DvmConsole.Application;

/// <summary>
/// Owns operator receive selection, output-mute application, and output-route
/// recovery using stable IDs, immutable channel state, and host callbacks.
/// </summary>
internal sealed class ReceiveOutputController : IAsyncDisposable
{
    private readonly IReceiveOutputRoutePort routes;
    private readonly IReceiveOutputMutePort mute;
    private readonly IReceiveOutputPresentationPort presentation;
    private readonly IReceiveOutputLifetimePort lifetimePort;
    private readonly object recoverySync = new();
    private readonly HashSet<ChannelId> proactiveRecoveries = [];
    private readonly HashSet<Task> recoveryTasks = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncDisposal disposal = new();
    private bool disposeStarted;

    public ReceiveOutputController(
        IReceiveOutputRoutePort routes,
        IReceiveOutputMutePort mute,
        IReceiveOutputPresentationPort presentation,
        IReceiveOutputLifetimePort lifetime)
    {
        this.routes = routes ?? throw new ArgumentNullException(nameof(routes));
        this.mute = mute ?? throw new ArgumentNullException(nameof(mute));
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        lifetimePort = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    }

    public async Task StartAsync(
        ChannelId channel,
        bool persistSelection,
        CancellationToken cancellationToken = default)
    {
        ReceiveOutputChannelState state = presentation.Capture(channel);
        // Operator intent survives a temporarily unavailable output device.
        if (persistSelection)
        {
            await presentation.RunAsync(() =>
            {
                presentation.SetAudioEnabled(channel, true);
                SetSelectionPreference(channel, enabled: true);
            }).ConfigureAwait(false);
        }
        bool audioStarted = false;
        bool queueStarted = false;
        try
        {
            await routes.StartAsync(state.Channel, cancellationToken).ConfigureAwait(false);
            audioStarted = true;
            if (mute.IsMuted(channel))
            {
                await routes
                    .SetLivePlaybackEnabledAsync(channel, enabled: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            routes.StartWork(channel);
            queueStarted = true;
            routes.ResetDiagnostics(channel);
            await presentation.RunAsync(() =>
            {
                presentation.SetAudioEnabled(channel, true);
                if (persistSelection)
                    SetSelectionPreference(channel, enabled: true);
                presentation.PublishStatus($"Listening to {state.Name} ({state.ModeText}); " +
                    $"{routes.LivePlaybackChannels.Count} channel(s) active.");
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Exception? rollbackFailure = null;
            if (queueStarted)
            {
                try
                {
                    await routes.StopWorkAsync(channel).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    rollbackFailure = rollbackException;
                }
            }
            if (audioStarted)
            {
                try
                {
                    await routes.StopAsync(channel, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    rollbackFailure ??= rollbackException;
                }
            }

            routes.RecordFailure(channel, lifetimePort.UtcNow.AddSeconds(5));
            await presentation.RunAsync(() =>
            {
                string rollbackText = rollbackFailure is null
                    ? string.Empty
                    : $" Cleanup also failed: {rollbackFailure.Message}";
                presentation.PublishStatus(
                    $"RX audio unavailable: {exception.Message}.{rollbackText}".TrimEnd('.'));
            }).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(
        ChannelId channel,
        bool persistSelection,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        bool keptForRecording = presentation.Capture(channel).RecordingEnabled;
        try
        {
            if (keptForRecording)
            {
                await routes
                    .SetLivePlaybackEnabledAsync(channel, enabled: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                try
                {
                    // Silence the physical route before retiring decode work.
                    // A cancellation-ignoring decoder can then finish against
                    // its retained worker lifetime without producing late
                    // speaker output or delaying route cleanup indefinitely.
                    await routes
                        .SetLivePlaybackEnabledAsync(channel, enabled: false, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
                try
                {
                    await routes.StopWorkAsync(channel).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
                routes.ResetDiagnostics(channel);
                try
                {
                    await routes.StopAsync(channel, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            bool liveSelectionStillActive = !keptForRecording && routes.IsActive(channel);
            await presentation.RunAsync(() =>
            {
                if (!keptForRecording && !liveSelectionStillActive)
                    presentation.StopRecording(channel);
                presentation.SetAudioEnabled(channel, liveSelectionStillActive);
                if (persistSelection)
                    SetSelectionPreference(channel, enabled: liveSelectionStillActive);
                presentation.PublishStatus(failures.Count > 0
                    ? $"RX audio could not be fully stopped: {failures[0].Message}"
                    : routes.LivePlaybackChannels.Count == 0
                    ? "RX audio disabled."
                    : $"Listening to {routes.LivePlaybackChannels.Count} channel(s).");
            }).ConfigureAwait(false);
        }

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("RX audio stop encountered multiple failures.", failures);
    }

    public async ValueTask SetEnabledAsync(ChannelId channel, bool enabled,
        CancellationToken cancellationToken = default)
        => await SetSelectionAsync([channel], enabled, cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>Serializes single, toggle and bulk receive selection with output reconfiguration.</summary>
    public async Task SetSelectionAsync(IReadOnlyList<ChannelId> channels, bool? enabled,
        Func<ChannelId, bool, CancellationToken, Task>? prepare = null,
        Func<ChannelId, Exception, Task>? reportFailure = null,
        CancellationToken cancellationToken = default)
    {
        await routes.RunExclusiveAsync(async token =>
        {
            foreach (ChannelId channel in channels)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(lifetimePort.IsDisposing, this);
                try
                {
                    ReceiveOutputChannelState state = presentation.Capture(channel);
                    bool target = enabled ?? !state.AudioEnabled;
                    if (prepare is not null) await prepare(channel, target, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(lifetimePort.IsDisposing, this);
                    if (target)
                    {
                        bool missingOutput = mute.ShouldEnableLivePlayback(channel, state.AudioSuspended) &&
                            !routes.LivePlaybackChannels.Contains(channel);
                        if (!state.AudioEnabled || !routes.IsActive(channel) || missingOutput)
                            await StartAsync(channel, persistSelection: true, token).ConfigureAwait(false);
                    }
                    else if (state.AudioEnabled)
                        await StopAsync(channel, persistSelection: true, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (reportFailure is not null && !token.IsCancellationRequested)
                {
                    await reportFailure(channel, exception).ConfigureAwait(false);
                }
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public void SetSelectionPreference(ChannelId channel, bool enabled)
    {
        presentation.SetSelectionPreference(channel, enabled);
    }

    public string? GetEffectiveMuteReason(ChannelId channel, bool outputMuted)
    {
        return presentation.Capture(channel).AudioSuspended
            ? "console transmit mute"
            : mute.GetEffectiveReason(channel, outputMuted);
    }

    public async Task ApplySystemMuteAsync(
        string systemName, IReadOnlyCollection<ChannelId> channels, bool muted)
    {
        await ApplyMutePolicyAsync(channels).ConfigureAwait(false);
        await presentation.RunAsync(() =>
        {
            presentation.NotifyMuteChanged();
            presentation.PublishStatus(muted
                ? $"Live RX output for {systemName} is muted; decoding and TAR continue."
                : $"Live RX output for {systemName} is restored except for any muted zones.");
        }).ConfigureAwait(false);
    }

    public async Task ApplyZoneMuteAsync(
        string zoneName, IReadOnlyCollection<ChannelId> channels, bool muted)
    {
        await ApplyMutePolicyAsync(channels).ConfigureAwait(false);
        await presentation.RunAsync(() =>
        {
            presentation.NotifyMuteChanged();
            presentation.PublishStatus(muted
                ? $"Live RX output for zone {zoneName} is muted; decoding and TAR continue."
                : $"Live RX output for zone {zoneName} is restored except for any muted system scope.");
        }).ConfigureAwait(false);
    }

    public async Task<ReceiveRouteRecoveryResult> RecoverSelectedAsync(
        ChannelId failedChannel,
        object? expectedSession = null,
        CancellationToken cancellationToken = default)
    {
        return await routes.RunExclusiveAsync(async exclusiveCancellationToken =>
        {
            if (expectedSession is not null &&
                !ReferenceEquals(expectedSession, routes.GetSessionIdentity(failedChannel)))
                return new ReceiveRouteRecoveryResult([], [], null);

            ReceiveRouteRecoveryResult result = await routes
                .RecoverSelectedAsync([failedChannel], exclusiveCancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset retryAt = lifetimePort.UtcNow.AddSeconds(5);
            foreach (ChannelId channel in result.Restarted)
            {
                if (mute.IsMuted(channel))
                {
                    await routes
                        .SetLivePlaybackEnabledAsync(
                            channel,
                            enabled: false,
                            exclusiveCancellationToken)
                        .ConfigureAwait(false);
                }
                routes.RecordRestarted(channel);
                routes.StartWork(channel);
                routes.ResetDiagnostics(channel);
            }
            foreach (ChannelId channel in result.Failed)
                routes.RecordFailure(channel, retryAt);
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public void HandleOutputFailed(ReceiveAudioOutputFailure failure)
    {
        Task recovery;
        lock (recoverySync)
        {
            if (disposeStarted || lifetimePort.IsDisposing)
                return;
            ChannelId[] pending = failure.AffectedChannels
                .Where(channel => proactiveRecoveries.Add(channel))
                .ToArray();
            if (pending.Length == 0)
                return;
            var identities = pending.ToDictionary(id => id, routes.GetSessionIdentity);
            recovery = RecoverFailedOutputAsync(failure with { AffectedChannels = pending }, identities, lifetime.Token);
            recoveryTasks.Add(recovery);
        }
        TaskObservation.Observe(CompleteTrackedRecoveryAsync(recovery));
    }

    // Recovery is owned here and starts after the receive worker releases its lease.
    public void RequestRecovery(ChannelId channel, Exception failure)
        => HandleOutputFailed(new ReceiveAudioOutputFailure(string.Empty, [channel], failure));

    public bool IsRecoveryRunning(ChannelId channel)
    {
        lock (recoverySync)
            return proactiveRecoveries.Contains(channel);
    }

    public Task ReconcileAsync(CancellationToken cancellationToken = default)
        => routes.ReconcileAsync(cancellationToken);

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task CompleteTrackedRecoveryAsync(Task recovery)
    {
        try
        {
            await recovery.ConfigureAwait(false);
        }
        finally
        {
            lock (recoverySync)
                recoveryTasks.Remove(recovery);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task[] pending;
        lock (recoverySync)
        {
            disposeStarted = true;
            lifetime.Cancel();
            pending = recoveryTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch
        {
            // Each recovery reports its own failure while the session is live.
            // Disposal owns only cancellation and completion observation.
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task RecoverFailedOutputAsync(
        ReceiveAudioOutputFailure failure,
        IReadOnlyDictionary<ChannelId, object?> identities,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        long recoveryStarted = lifetimePort.GetTimestamp();
        try
        {
            ChannelId? failedChannelId = failure.AffectedChannels
                .Where(id => identities[id] is not null && ReferenceEquals(identities[id], routes.GetSessionIdentity(id)))
                .Select(static channelId => (ChannelId?)channelId)
                .FirstOrDefault();
            if (failedChannelId is null || lifetimePort.IsDisposing || cancellationToken.IsCancellationRequested)
                return;

            ReceiveRouteRecoveryResult recovery = await RecoverSelectedAsync(
                    failedChannelId.Value,
                    identities[failedChannelId.Value],
                    cancellationToken)
                .ConfigureAwait(false);
            if (recovery.Restarted.Count == 0 && recovery.Failed.Count == 0)
                return;
            lifetimePort.ObserveRecovery(
                lifetimePort.GetElapsedTime(recoveryStarted),
                DescribeRecovery(recovery));
            await presentation.RunAsync(() => presentation.PublishStatus(recovery.Failed.Count == 0
                ? $"RX audio restarted for {recovery.Restarted.Count} selected channel(s) after the output callback stopped."
                : recovery.Diagnostic ?? "RX audio unavailable; retrying selected channels."))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!lifetimePort.IsDisposing)
        {
            lifetimePort.ObserveRecovery(
                lifetimePort.GetElapsedTime(recoveryStarted),
                $"failed: {exception.Message}");
            await presentation.RunAsync(() =>
                presentation.PublishStatus($"RX audio recovery failed: {exception.Message}"))
                .ConfigureAwait(false);
        }
        finally
        {
            lock (recoverySync)
            {
                foreach (ChannelId channel in failure.AffectedChannels)
                    proactiveRecoveries.Remove(channel);
            }
        }
    }

    private async Task ApplyMutePolicyAsync(
        IEnumerable<ChannelId> channels,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        (ChannelId ChannelId, bool Enabled)[] changes = channels
            .Distinct()
            .Select(channel => (
                channel,
                mute.ShouldEnableLivePlayback(
                    channel,
                    isTemporarilySuspended: presentation.Capture(channel).AudioSuspended)))
            .ToArray();
        try
        {
            await routes.ApplyPlaybackPolicyAsync(changes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Receive-output policy reconciliation failed.", failures);
    }

    private static string DescribeRecovery(ReceiveRouteRecoveryResult recovery)
        => recovery.Failed.Count == 0
            ? $"restarted {recovery.Restarted.Count} route(s)"
            : recovery.Diagnostic ?? $"failed {recovery.Failed.Count} route(s)";
}
