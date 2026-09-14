// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

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

    public Task StartAsync(ChannelId channel)
        => receiveOutput.StartAsync(channel, persistSelection: false);
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
