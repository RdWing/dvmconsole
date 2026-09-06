// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using System.Diagnostics;

namespace DvmConsole.Desktop;

/// <summary>
/// Owns operator receive selection, output-mute application, and output-route
/// recovery. The main view model remains the binding facade and supplies only
/// UI publication and persistence callbacks.
/// </summary>
internal sealed class ReceiveOutputController : IAsyncDisposable
{
    private readonly IReceiveOutputRoutePort routes;
    private readonly IReceiveOutputMutePort mute;
    private readonly IReceiveOutputPresentationPort presentation;
    private readonly IReceiveOutputLifetimePort lifetimePort;
    private readonly object recoverySync = new();
    private readonly HashSet<ChannelViewModel> proactiveRecoveries = [];
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
        ChannelViewModel channel,
        bool persistSelection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        // Starting RX records operator intent independently from device availability.
        if (persistSelection)
        {
            await presentation.RunAsync(() =>
            {
                channel.SetAudioEnabled(true, "operator RX selection");
                SetSelectionPreference(channel, enabled: true);
            }).ConfigureAwait(false);
        }
        bool audioStarted = false;
        bool queueStarted = false;
        try
        {
            await routes.StartAsync(channel, cancellationToken).ConfigureAwait(false);
            audioStarted = true;
            if (mute.IsMuted(channel))
            {
                await routes
                    .SetLivePlaybackEnabledAsync(channel.Id, enabled: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            routes.StartWork(channel.Id);
            queueStarted = true;
            routes.ResetDiagnostics(channel.Id);
            await presentation.RunAsync(() =>
            {
                channel.SetAudioEnabled(true, persistSelection ? "operator RX start" : "RX restore/recovery");
                if (persistSelection)
                    SetSelectionPreference(channel, enabled: true);
                presentation.PublishStatus($"Listening to {channel.Name} ({channel.ModeText}); " +
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
                    await routes.StopWorkAsync(channel.Id).ConfigureAwait(false);
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
                    await routes.StopAsync(channel.Id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    rollbackFailure ??= rollbackException;
                }
            }

            routes.RecordFailure(channel.Id, DateTimeOffset.UtcNow.AddSeconds(5));
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
        ChannelViewModel channel,
        bool persistSelection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var failures = new List<Exception>();
        bool keptForRecording = channel.IsRecordingEnabled;
        try
        {
            if (keptForRecording)
            {
                await routes
                    .SetLivePlaybackEnabledAsync(channel.Id, enabled: false, cancellationToken)
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
                        .SetLivePlaybackEnabledAsync(channel.Id, enabled: false, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
                try
                {
                    await routes.StopWorkAsync(channel.Id).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
                routes.ResetDiagnostics(channel.Id);
                try
                {
                    await routes.StopAsync(channel.Id, cancellationToken).ConfigureAwait(false);
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
            bool liveSelectionStillActive = !keptForRecording && routes.IsActive(channel.Id);
            await presentation.RunAsync(() =>
            {
                if (!keptForRecording && !liveSelectionStillActive)
                    presentation.StopRecording(channel);
                channel.SetAudioEnabled(liveSelectionStillActive, persistSelection ? "operator RX stop reconciliation" : "RX stop reconciliation");
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

    public async ValueTask SetEnabledAsync(
        ChannelViewModel channel,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled == channel.IsAudioEnabled)
            return;

        if (enabled)
            await StartAsync(channel, persistSelection: true, cancellationToken).ConfigureAwait(false);
        else
            await StopAsync(channel, persistSelection: true, cancellationToken).ConfigureAwait(false);
    }

    public void SetSelectionPreference(ChannelViewModel channel, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(channel);
        presentation.SetSelectionPreference(channel, enabled);
    }

    public string? GetEffectiveMuteReason(ChannelViewModel channel, bool outputMuted)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.IsAudioSuspended
            ? "console transmit mute"
            : mute.GetEffectiveReason(channel, outputMuted);
    }

    public async Task ToggleSystemMuteAsync(SystemViewModel? system)
    {
        if (system is null)
            return;

        bool muted = mute.Toggle(system);
        await ApplyMutePolicyAsync(system.Channels).ConfigureAwait(false);
        await presentation.RunAsync(() =>
        {
            presentation.NotifyMuteChanged();
            presentation.PublishStatus(muted
                ? $"Live RX output for {system.Name} is muted; decoding and TAR continue."
                : $"Live RX output for {system.Name} is restored except for any muted zones.");
        }).ConfigureAwait(false);
    }

    public async Task ToggleZoneMuteAsync(ZoneViewModel? zone)
    {
        if (zone is null)
            return;

        bool muted = mute.Toggle(zone);
        await ApplyMutePolicyAsync(zone.Channels).ConfigureAwait(false);
        await presentation.RunAsync(() =>
        {
            presentation.NotifyMuteChanged();
            presentation.PublishStatus(muted
                ? $"Live RX output for zone {zone.Name} is muted; decoding and TAR continue."
                : $"Live RX output for zone {zone.Name} is restored except for any muted system scope.");
        }).ConfigureAwait(false);
    }

    public async Task<ReceiveRouteRecoveryResult> RecoverSelectedAsync(
        ChannelViewModel failedChannel,
        object? expectedSession = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failedChannel);
        return await routes.RunExclusiveAsync(async exclusiveCancellationToken =>
        {
            if (expectedSession is not null &&
                !ReferenceEquals(expectedSession, routes.GetSessionIdentity(failedChannel.Id)))
                return new ReceiveRouteRecoveryResult([], [], null);

            ReceiveRouteRecoveryResult result = await routes
                .RecoverSelectedAsync([failedChannel.Id], exclusiveCancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset retryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            foreach (ChannelViewModel channel in presentation.Resolve(result.Restarted))
            {
                if (mute.IsMuted(channel))
                {
                    await routes
                        .SetLivePlaybackEnabledAsync(
                            channel.Id,
                            enabled: false,
                            exclusiveCancellationToken)
                        .ConfigureAwait(false);
                }
                routes.RecordRestarted(channel.Id);
                routes.StartWork(channel.Id);
                routes.ResetDiagnostics(channel.Id);
            }
            foreach (ChannelViewModel channel in presentation.Resolve(result.Failed))
                routes.RecordFailure(channel.Id, retryAt);
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
            ChannelId[] pending = presentation.Resolve(failure.AffectedChannels)
                .Where(channel => proactiveRecoveries.Add(channel))
                .Select(channel => channel.Id)
                .ToArray();
            if (pending.Length == 0)
                return;
            var identities = pending.ToDictionary(id => id, routes.GetSessionIdentity);
            recovery = RecoverFailedOutputAsync(failure with { AffectedChannels = pending }, identities, lifetime.Token);
            recoveryTasks.Add(recovery);
        }
        TaskObservation.Observe(CompleteTrackedRecoveryAsync(recovery));
    }

    // Called by a receive worker after its processing lease is released. Recovery
    // belongs to this controller, so the worker never joins its shutdown caller's gate.
    public void RequestRecovery(ChannelViewModel channel, Exception failure)
        => HandleOutputFailed(new ReceiveAudioOutputFailure(string.Empty, [channel.Id], failure));

    public bool IsRecoveryRunning(ChannelViewModel channel)
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
        long recoveryStarted = Stopwatch.GetTimestamp();
        try
        {
            ChannelId? failedChannelId = failure.AffectedChannels
                .Where(id => identities[id] is not null && ReferenceEquals(identities[id], routes.GetSessionIdentity(id)))
                .Select(static channelId => (ChannelId?)channelId)
                .FirstOrDefault();
            if (failedChannelId is null || lifetimePort.IsDisposing || cancellationToken.IsCancellationRequested)
                return;

            ReceiveRouteRecoveryResult recovery = await RecoverSelectedAsync(
                    presentation.Resolve(failedChannelId.Value),
                    identities[failedChannelId.Value],
                    cancellationToken)
                .ConfigureAwait(false);
            if (recovery.Restarted.Count == 0 && recovery.Failed.Count == 0)
                return;
            lifetimePort.ObserveRecovery(
                Stopwatch.GetElapsedTime(recoveryStarted),
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
                Stopwatch.GetElapsedTime(recoveryStarted),
                $"failed: {exception.Message}");
            await presentation.RunAsync(() =>
                presentation.PublishStatus($"RX audio recovery failed: {exception.Message}"))
                .ConfigureAwait(false);
        }
        finally
        {
            lock (recoverySync)
            {
                foreach (ChannelViewModel channel in presentation.Resolve(failure.AffectedChannels))
                    proactiveRecoveries.Remove(channel);
            }
        }
    }

    private async Task ApplyMutePolicyAsync(
        IEnumerable<ChannelViewModel> channels,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        (ChannelId ChannelId, bool Enabled)[] changes = channels
            .Distinct()
            .Select(channel => (
                channel.Id,
                mute.ShouldEnableLivePlayback(
                    channel,
                    isTemporarilySuspended: channel.IsAudioSuspended)))
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
