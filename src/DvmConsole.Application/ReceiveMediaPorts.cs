// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

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
    bool EnqueueAudio(ChannelId channel, IRadioMediaFrame traffic, long ingressTimestamp, out bool droppedFrame);
    bool EnqueuePatch(ChannelId channel, IRadioMediaFrame traffic, long? ingressTimestamp);
    void StopPatchSource(ChannelId channel, uint streamId);
    Task RunAfterStreamAsync(ChannelId channel, uint streamId, Func<CancellationToken, Task> continuation);
    Task CompleteStreamAsync(ChannelId channel, uint streamId, DateTimeOffset endedAt, CancellationToken cancellationToken);
}
