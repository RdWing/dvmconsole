// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

internal sealed class ReceiveMediaPort(
    ChannelReceiveAudioCoordinator audio,
    ChannelReceiveWorkQueue audioWork,
    PatchSourceDecodeCoordinator patch,
    ChannelReceiveWorkQueue patchWork,
    PatchForwardingCoordinator forwarding,
    Func<bool> isDisposing,
    Func<ChannelId, IRadioMediaFrame, long?, bool>? enqueuePatch = null) : IReceiveMediaState, IReceiveMediaWork
{
    public bool IsDisposing => isDisposing();
    public IReadOnlyList<ChannelId> ActiveAudioChannels => audio.ActiveChannels;
    public IReadOnlyList<ChannelId> ActivePatchChannels => patch.ActiveChannels;
    public bool IsAudioActive(ChannelId channel) => audio.IsActive(channel);
    public bool IsPatchActive(ChannelId channel) => patch.IsActive(channel);
    public bool IsAudioTrackingStream(ChannelId channel, uint streamId) => audio.IsTrackingStream(channel, streamId);
    public bool IsPatchTrackingStream(ChannelId channel, uint streamId) => patch.IsTrackingStream(channel, streamId);
    public bool EnqueueAudio(ChannelId channel, IRadioMediaFrame traffic, long ingressTimestamp, out bool droppedFrame)
        => audioWork.Enqueue(channel, RadioMediaIngressFrame.FromFrame(traffic, ingressTimestamp), out droppedFrame);
    public bool EnqueuePatch(ChannelId channel, IRadioMediaFrame traffic, long? ingressTimestamp)
    {
        if (enqueuePatch is not null) return enqueuePatch(channel, traffic, ingressTimestamp);
        patchWork.Start(channel);
        return ingressTimestamp is long timestamp
            ? patchWork.Enqueue(channel, RadioMediaIngressFrame.FromFrame(traffic, timestamp), out _)
            : patchWork.Enqueue(channel, RadioMediaIngressFrame.FromFrame(traffic), out _);
    }
    public void StopPatchSource(ChannelId channel, uint streamId) => forwarding.StopSource(channel, streamId);
    public Task RunAfterStreamAsync(ChannelId channel, uint streamId, Func<CancellationToken, Task> continuation)
        => audioWork.RunAfterStreamAsync(channel, streamId, continuation);
    public Task CompleteStreamAsync(ChannelId channel, uint streamId, DateTimeOffset endedAt, CancellationToken cancellationToken)
        => audio.CompleteStreamAsync(channel, streamId, endedAt, cancellationToken);
}
