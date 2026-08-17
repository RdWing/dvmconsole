using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Represents one explicit NXDN 4800 call: duplicated FACCH VCALL, voice, then
// duplicated FACCH TX_REL on the same FNE stream.
public sealed class NxdnTxCallSession : IDisposable
{
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
        byte[] header = NxdnVoicePacketCodec.CreateCallControlPacket(
            sourceId,
            destinationId,
            group,
            NxdnVoicePacketCodec.VoiceCallMessageType,
            audio.FrameSequence,
            cipherType: privacy?.AlgorithmId ?? 0,
            keyId: privacy?.KeyId ?? 0);
        send(header, audio.PacketSequence, streamId);
        audio.AdvanceSequence();
        if (privacy is not null && privacy.AlgorithmId != NxdnPrivacyAlgorithms.Ehr)
        {
            byte[] iv = NxdnVoicePacketCodec.CreateCallControlPacket(
                sourceId,
                destinationId,
                group,
                NxdnVoicePacketCodec.VoiceCallIvMessageType,
                audio.FrameSequence,
                messageIndicator: privacy.MessageIndicator.Span);
            send(iv, audio.PacketSequence, streamId);
            audio.AdvanceSequence();
        }
        started = true;
    }

    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || ended)
            throw new InvalidOperationException("The NXDN call must be active before processing audio.");
        return audio.Process(samples);
    }

    public void End()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
            throw new InvalidOperationException("The NXDN call has not started.");
        if (ended)
            return;
        audio.CompleteFrame();
        byte[] terminator = NxdnVoicePacketCodec.CreateCallControlPacket(
            sourceId, destinationId, group, NxdnVoicePacketCodec.TransmitReleaseMessageType, audio.FrameSequence);
        send(terminator, audio.PacketSequence, streamId);
        audio.AdvanceSequence();
        ended = true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        audio.Dispose();
        disposed = true;
    }
}
