// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed record ReceiveOutputChannelState(
    ReceiveChannelDescriptor Channel,
    bool AudioEnabled,
    bool RecordingEnabled,
    bool AudioSuspended)
{
    public string Name => Channel.Definition.Name;
    public string ModeText => Channel.Definition.Mode.ToUpperInvariant();
}

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
    bool IsMuted(ChannelId channelId);
    bool ShouldEnableLivePlayback(ChannelId channelId, bool isTemporarilySuspended);
    string? GetEffectiveReason(ChannelId channelId, bool outputMuted);
}

internal interface IReceiveOutputPresentationPort
{
    ReceiveOutputChannelState Capture(ChannelId channelId);
    void SetAudioEnabled(ChannelId channelId, bool enabled);
    Task RunAsync(Action action);
    void SetSelectionPreference(ChannelId channelId, bool enabled);
    void StopRecording(ChannelId channelId);
    void NotifyMuteChanged();
    void PublishStatus(string text);
}

internal interface IReceiveOutputLifetimePort
{
    DateTimeOffset UtcNow { get; }
    long GetTimestamp();
    TimeSpan GetElapsedTime(long started);
    bool IsDisposing { get; }
    void ObserveRecovery(TimeSpan elapsed, string result);
}
