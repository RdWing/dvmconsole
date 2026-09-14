// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Application;

internal interface ITransmitReceiveRoutePort
{
    IReadOnlyList<ChannelId> LivePlaybackChannels { get; }
    long SetLivePlaybackDiscarded(bool discarded);
    bool IsActive(ChannelId channelId);
    Task SetGainAsync(ChannelId channelId, double gain);
    Task SetLivePlaybackEnabledAsync(ChannelId channelId, bool enabled);
    Task StartAsync(ChannelId channel);
}

internal interface ITransmitReceiveMutePort
{
    bool ShouldEnableLivePlayback(ChannelId channel, bool isTemporarilySuspended);
}

internal interface ITransmitPermitTonePort
{
    Task<LocalTonePlaybackResult> PlayAsync(LocalTonePlaybackRequest request);
    Task<LocalTonePlaybackResult> PlayTalkPermitAsync(
        bool microphoneStartedCold,
        bool? microphoneIsBluetooth);
}

internal interface ITransmitAudioPresentationPort
{
    DateTimeOffset Now { get; }
    ReceiveOutputChannelState Capture(ChannelId channelId);
    void SetAudioSuspended(ChannelId channelId, bool suspended);
    double GetVolume(ChannelId channel);
    Task RunAsync(Action action);
    void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message);
    void PublishStatus(string text);
}

internal interface ITransmitAudioGate
{
    Task RunAsync(Func<Task> operation);
}
