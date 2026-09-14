// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

// Coordinates patch call state and decoding at the ordered receive-worker
// boundary. The jitter buffer is the sole packet-ordering authority, so a
// terminator observed here follows every earlier PCM block that can still be
// forwarded for the physical stream.
internal sealed class PatchSourceReceivePipeline
{
    private readonly PatchSourceDecodeCoordinator decoder;
    private readonly PatchForwardingCoordinator forwarding;

    public PatchSourceReceivePipeline(
        PatchSourceDecodeCoordinator decoder,
        PatchForwardingCoordinator forwarding)
    {
        this.decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        this.forwarding = forwarding ?? throw new ArgumentNullException(nameof(forwarding));
    }

    public async Task<int> ProcessAsync(
        ChannelId channel,
        IRadioMediaFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traffic);

        try
        {
            // Retired queued work must not key a target before the decoder
            // observes cancellation. Terminators still release ownership below.
            cancellationToken.ThrowIfCancellationRequested();
            forwarding.ObserveTraffic(channel, traffic);
            return await decoder.ProcessAsync(channel, traffic, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (RadioReceiveTrafficClassifier.IsTerminator(traffic))
                forwarding.StopSource(channel, traffic.StreamId);
        }
    }
}
