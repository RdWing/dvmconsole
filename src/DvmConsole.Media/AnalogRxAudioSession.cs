// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Media;

// Routes selected FNE analog PCM frames directly to the shared playback path.
public sealed class AnalogRxAudioSession : IAsyncDisposable
{
    private readonly AnalogTrafficSelector selector;
    private readonly IAudioPlayback playback;
    // ChannelReceiveAudioCoordinator serializes access to a receive session.
    // Retaining the packet buffer therefore removes one allocation per packet
    // without exposing mutable samples outside the awaited playback write.
    private readonly short[] packetSamples = new short[AnalogVoicePacketCodec.SamplesPerPacket];
    private bool disposed;

    public AnalogRxAudioSession(AnalogTrafficSelector selector, IAudioPlayback playback)
    {
        this.selector = selector ?? throw new ArgumentNullException(nameof(selector));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public int FramesDecoded { get; private set; }
    public long MalformedPackets { get; private set; }

    public async ValueTask<int> ProcessAsync(IRadioMediaFrame traffic, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);
        if (!selector.Matches(traffic))
            return 0;

        if (!AnalogVoicePacketCodec.TryExtractPcm(traffic.Payload, packetSamples))
        {
            MalformedPackets++;
            return 0;
        }

        await LivePacketAudioWriter.WriteAsync(playback, packetSamples, cancellationToken)
            .ConfigureAwait(false);
        FramesDecoded++;
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        await playback.DisposeAsync().ConfigureAwait(false);
        disposed = true;
    }
}
