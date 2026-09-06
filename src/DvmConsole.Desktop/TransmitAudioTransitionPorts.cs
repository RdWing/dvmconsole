// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Desktop;

internal interface ITransmitReceiveRoutePort
{
    IReadOnlyList<ChannelId> LivePlaybackChannels { get; }
    long SetLivePlaybackDiscarded(bool discarded);
    bool IsActive(ChannelId channelId);
    Task SetGainAsync(ChannelId channelId, double gain);
    Task SetLivePlaybackEnabledAsync(ChannelId channelId, bool enabled);
    Task StartAsync(ChannelViewModel channel);
}

internal interface ITransmitReceiveMutePort
{
    bool ShouldEnableLivePlayback(ChannelViewModel channel, bool isTemporarilySuspended);
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
    ChannelViewModel[] Resolve(IEnumerable<ChannelId> channelIds);
    double GetVolume(ChannelViewModel channel);
    Task RunAsync(Action action);
    void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message);
    void PublishStatus(string text);
}

internal interface ITransmitAudioGate
{
    Task RunAsync(Func<Task> operation);
}

internal sealed class TransmitReceiveRoutePort(
    ChannelReceiveAudioCoordinator audio,
    ReceiveOutputController receiveOutput) : ITransmitReceiveRoutePort
{
    public IReadOnlyList<ChannelId> LivePlaybackChannels => audio.LivePlaybackChannels;

    public long SetLivePlaybackDiscarded(bool discarded)
        => audio.SetLivePlaybackDiscarded(discarded);

    public bool IsActive(ChannelId channelId) => audio.IsActive(channelId);

    public Task SetGainAsync(ChannelId channelId, double gain)
        => audio.SetGainAsync(channelId, gain);

    public Task SetLivePlaybackEnabledAsync(ChannelId channelId, bool enabled)
        => audio.SetLivePlaybackEnabledAsync(channelId, enabled);

    public Task StartAsync(ChannelViewModel channel)
        => receiveOutput.StartAsync(channel, persistSelection: false);
}

internal sealed class TransmitReceiveMutePort(ReceiveOutputMutePolicy policy) : ITransmitReceiveMutePort
{
    public bool ShouldEnableLivePlayback(ChannelViewModel channel, bool isTemporarilySuspended)
        => policy.ShouldEnableLivePlayback(channel, isTemporarilySuspended);
}

internal sealed class TransmitPermitTonePort(LocalTonePlayer player) : ITransmitPermitTonePort
{
    public Task<LocalTonePlaybackResult> PlayAsync(LocalTonePlaybackRequest request)
        => player.PlayAsync(request);

    public Task<LocalTonePlaybackResult> PlayTalkPermitAsync(
        bool microphoneStartedCold,
        bool? microphoneIsBluetooth)
        => player.PlayTalkPermitAsync(microphoneStartedCold, microphoneIsBluetooth);
}

internal sealed class TransmitAudioPresentationPort(
    Func<IEnumerable<ChannelId>, ChannelViewModel[]> resolveChannels,
    Func<ChannelViewModel, double> getChannelVolume,
    Func<Action, Task> runOnUiThread,
    Action<DateTimeOffset, string, DebugLogSeverity, string> log,
    Action<string> publishAudioStatus) : ITransmitAudioPresentationPort
{
    public ChannelViewModel[] Resolve(IEnumerable<ChannelId> channelIds)
        => resolveChannels(channelIds);

    public double GetVolume(ChannelViewModel channel) => getChannelVolume(channel);

    public Task RunAsync(Action action) => runOnUiThread(action);

    public void Log(
        DateTimeOffset timestamp,
        string source,
        DebugLogSeverity severity,
        string message)
        => log(timestamp, source, severity, message);

    public void PublishStatus(string text) => publishAudioStatus(text);
}

internal sealed class TransmitAudioGate(SemaphoreSlim gate) : ITransmitAudioGate
{
    public async Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
