// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Combines operational service state without consulting bound channel models.</summary>
public sealed class ConsoleSnapshotContextSource(
    IReceiveRecordingStateSource? recordings,
    Func<ChannelId, bool, string?> muteReason,
    Func<bool> outputMuted,
    Func<ChannelId?> playbackChannel,
    Func<IReadOnlyDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>>> patchMemberships,
    bool allowTransmitControls = true)
{
    public IReadOnlyDictionary<ChannelId, ChannelSnapshotContext> Capture(IReadOnlyList<ChannelId> channels)
    {
        // Capture once per update, rather than querying services for each projected field.
        var recordingStates = recordings?.CaptureStates(channels);
        var patches = patchMemberships();
        bool globallyMuted = outputMuted();
        ChannelId? playing = playbackChannel();
        var result = new Dictionary<ChannelId, ChannelSnapshotContext>(channels.Count);
        foreach (ChannelId id in channels)
        {
            ReceiveRecordingState recording = recordingStates?.GetValueOrDefault(id) ?? default;
            result.Add(id, new(recording.IsRecording, recording.IsFinalizing, recording.Fault,
                muteReason(id, globallyMuted), playing == id,
                patches.GetValueOrDefault(id), allowTransmitControls));
        }
        return result;
    }
}
