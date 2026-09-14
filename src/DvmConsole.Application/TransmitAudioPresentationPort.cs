// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Application;

internal sealed class TransmitAudioPresentationPort(
    ConsoleChannelMediaDirectory channels,
    object synchronization,
    Action<ChannelId>? changed,
    Action<DateTimeOffset, string, DebugLogSeverity, string> log,
    Action<string> publishAudioStatus,
    IClock? clock = null) : ITransmitAudioPresentationPort
{
    public DateTimeOffset Now => (clock ?? SystemClock.Instance).UtcNow.ToLocalTime();

    public ReceiveOutputChannelState Capture(ChannelId channelId)
    {
        ConsoleChannelState channel = channels.State(channelId);
        ChannelOperatorSnapshot state = channel.Operator.Snapshot;
        return new(new(channelId, channel.Runtime.Definition), state.AudioEnabled, state.RecordingEnabled, state.AudioSuspended);
    }

    public void SetAudioSuspended(ChannelId channelId, bool suspended)
    {
        lock (synchronization)
            if (channels.State(channelId).SetAudioSuspended(suspended)) changed?.Invoke(channelId);
    }

    public double GetVolume(ChannelId channel) => channels.Gain(channel);

    public Task RunAsync(Action action)
    {
        lock (synchronization) action();
        return Task.CompletedTask;
    }

    public void Log(
        DateTimeOffset timestamp,
        string source,
        DebugLogSeverity severity,
        string message)
        => log(timestamp, source, severity, message);

    public void PublishStatus(string text) => publishAudioStatus(text);
}
