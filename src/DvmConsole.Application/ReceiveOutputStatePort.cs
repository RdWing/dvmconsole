// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal interface IReceiveOutputView
{
    void ReceiveSelectionChanged(ChannelId channel, bool previous, bool enabled);
    Task RunAsync(Action action);
    void SetSelectionPreference(ChannelId channel, bool enabled);
    void StopRecording(ChannelId channel);
    void NotifyMuteChanged();
    void PublishStatus(string text);
}

/// <summary>Keeps authoritative receive selection in Application; the host only presents it.</summary>
internal sealed class ReceiveOutputStatePort(
    IReadOnlyDictionary<ChannelId, ConsoleChannelState> channels, IReceiveOutputView view)
    : IReceiveOutputPresentationPort
{
    public ReceiveOutputChannelState Capture(ChannelId channelId)
    {
        ConsoleChannelState channel = channels[channelId];
        ChannelOperatorSnapshot state = channel.Operator.Snapshot;
        return new(new(channelId, channel.Runtime.Definition), state.AudioEnabled, state.RecordingEnabled, state.AudioSuspended);
    }

    public void SetAudioEnabled(ChannelId channelId, bool enabled)
    {
        ConsoleChannelState channel = channels[channelId];
        bool previous = channel.Operator.Snapshot.AudioEnabled;
        if (channel.SetReceiveEnabled(enabled)) view.ReceiveSelectionChanged(channelId, previous, enabled);
    }

    public Task RunAsync(Action action) => view.RunAsync(action);
    public void SetSelectionPreference(ChannelId channel, bool enabled) => view.SetSelectionPreference(channel, enabled);
    public void StopRecording(ChannelId channel) => view.StopRecording(channel);
    public void NotifyMuteChanged() => view.NotifyMuteChanged();
    public void PublishStatus(string text) => view.PublishStatus(text);
}
