using DvmConsole.FneClient;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

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
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);

        forwarding.ObserveTraffic(channel, traffic);
        try
        {
            return await decoder.ProcessAsync(channel, traffic, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ReceiveTrafficClassifier.IsTerminator(traffic))
                forwarding.StopSource(channel, traffic.StreamId);
        }
    }
}
