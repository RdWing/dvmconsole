using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Represents one explicit NXDN 4800 call: FACCH startup, continuous voice with
// SACCH call signaling, then duplicated FACCH TX_REL on the same FNE stream.
public sealed class NxdnTxCallSession : IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(80);
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly bool group;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly NxdnTxAudioSession audio;
    private readonly NxdnPrivacyOptions? privacy;
    private bool started;
    private bool ended;
    private bool disposed;

    public NxdnTxCallSession(
        uint sourceId,
        uint destinationId,
        bool group,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        NxdnPrivacyOptions? privacy = null)
    {
        this.sourceId = sourceId;
        this.destinationId = destinationId;
        this.group = group;
        this.streamId = streamId;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.privacy = privacy;
        audio = new NxdnTxAudioSession(sourceId, destinationId, group, streamId, vocoder, send, privacy: privacy);
    }

    public bool IsStarted => started;
    public bool IsEnded => ended;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            throw new InvalidOperationException("The NXDN call has already started.");
        if (ended)
            throw new InvalidOperationException("The NXDN call has already ended.");
        byte[] header = privacy is not null &&
            privacy.AlgorithmId is NxdnPrivacyAlgorithms.Des or NxdnPrivacyAlgorithms.Aes256
            ? NxdnVoicePacketCodec.CreatePrivacyCallStartPacket(
                sourceId,
                destinationId,
                group,
                audio.FrameSequence,
                privacy.AlgorithmId,
                privacy.KeyId,
                privacy.MessageIndicator.Span)
            : NxdnVoicePacketCodec.CreateCallControlPacket(
                sourceId,
                destinationId,
                group,
                NxdnVoicePacketCodec.VoiceCallMessageType,
                audio.FrameSequence,
                cipherType: privacy?.AlgorithmId ?? 0,
                keyId: privacy?.KeyId ?? 0);
        send(header, audio.PacketSequence, streamId);
        audio.AdvanceSequence();
        started = true;
    }

    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || ended)
            throw new InvalidOperationException("The NXDN call must be active before processing audio.");
        return audio.Process(samples);
    }

    public ValueTask EndAsync(CancellationToken cancellationToken = default)
        => EndAsync(WaitForNextFrameAsync, cancellationToken);

    internal async ValueTask EndAsync(
        Func<CancellationToken, ValueTask> waitForNextFrame,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(waitForNextFrame);
        if (!started)
            throw new InvalidOperationException("The NXDN call has not started.");
        if (ended)
            return;
        IReadOnlyList<NxdnOutboundPacket> completion = audio.PrepareFrameCompletion();
        foreach (NxdnOutboundPacket packet in completion)
        {
            await waitForNextFrame(cancellationToken).ConfigureAwait(false);
            send(packet.Payload, packet.Sequence, packet.StreamId);
        }
        await waitForNextFrame(cancellationToken).ConfigureAwait(false);
        byte[] terminator = NxdnVoicePacketCodec.CreateCallControlPacket(
            sourceId, destinationId, group, NxdnVoicePacketCodec.TransmitReleaseMessageType, audio.FrameSequence);
        send(terminator, audio.PacketSequence, streamId);
        audio.AdvanceSequence();
        ended = true;
    }

    private static async ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken)
        => await Task.Delay(FrameInterval, cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        if (disposed)
            return;
        audio.Dispose();
        disposed = true;
    }
}
