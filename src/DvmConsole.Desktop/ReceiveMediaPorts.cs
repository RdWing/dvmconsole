// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal interface IReceiveMediaState
{
    bool IsDisposing { get; }
    IReadOnlyList<ChannelId> ActiveAudioChannels { get; }
    IReadOnlyList<ChannelId> ActivePatchChannels { get; }
    bool IsAudioActive(ChannelId channel);
    bool IsPatchActive(ChannelId channel);
    bool IsAudioTrackingStream(ChannelId channel, uint streamId);
    bool IsPatchTrackingStream(ChannelId channel, uint streamId);
}

internal interface IReceiveMediaWork
{
    bool IsDisposing { get; }
    bool EnqueueAudio(ChannelId channel, FneTrafficFrame traffic, long ingressTimestamp, out bool droppedFrame);
    bool EnqueuePatch(ChannelId channel, FneTrafficFrame traffic, long? ingressTimestamp);
    void StopPatchSource(ChannelId channel, uint streamId);
    Task RunAfterStreamAsync(ChannelId channel, uint streamId, Func<CancellationToken, Task> continuation);
    Task CompleteStreamAsync(ChannelId channel, uint streamId, DateTimeOffset endedAt, CancellationToken cancellationToken);
}

internal sealed class DesktopReceiveMediaPort(
    ChannelReceiveAudioCoordinator audio,
    ChannelReceiveWorkQueue audioWork,
    PatchSourceDecodeCoordinator patch,
    ChannelReceiveWorkQueue patchWork,
    PatchForwardingCoordinator forwarding,
    Func<bool> isDisposing) : IReceiveMediaState, IReceiveMediaWork
{
    public bool IsDisposing => isDisposing();
    public IReadOnlyList<ChannelId> ActiveAudioChannels => audio.ActiveChannels;
    public IReadOnlyList<ChannelId> ActivePatchChannels => patch.ActiveChannels;
    public bool IsAudioActive(ChannelId channel) => audio.IsActive(channel);
    public bool IsPatchActive(ChannelId channel) => patch.IsActive(channel);
    public bool IsAudioTrackingStream(ChannelId channel, uint streamId) => audio.IsTrackingStream(channel, streamId);
    public bool IsPatchTrackingStream(ChannelId channel, uint streamId) => patch.IsTrackingStream(channel, streamId);
    public bool EnqueueAudio(ChannelId channel, FneTrafficFrame traffic, long ingressTimestamp, out bool droppedFrame)
        => audioWork.Enqueue(channel, traffic, ingressTimestamp, out droppedFrame);
    public bool EnqueuePatch(ChannelId channel, FneTrafficFrame traffic, long? ingressTimestamp)
    {
        patchWork.Start(channel);
        return ingressTimestamp is long timestamp
            ? patchWork.Enqueue(channel, traffic, timestamp, out _)
            : patchWork.Enqueue(channel, traffic, out _);
    }
    public void StopPatchSource(ChannelId channel, uint streamId) => forwarding.StopSource(channel, streamId);
    public Task RunAfterStreamAsync(ChannelId channel, uint streamId, Func<CancellationToken, Task> continuation)
        => audioWork.RunAfterStreamAsync(channel, streamId, continuation);
    public Task CompleteStreamAsync(ChannelId channel, uint streamId, DateTimeOffset endedAt, CancellationToken cancellationToken)
        => audio.CompleteStreamAsync(channel, streamId, endedAt, cancellationToken);
}
