using DvmConsole.Audio;

namespace DvmConsole.Media;

// Frames PCM into the 160-sample analog wire packets used by dvmhost.
// The first packet is `VOICE_START`; subsequent packets are `VOICE`.
// Call lifecycle and capture ownership remain outside this reusable media
// session.
public sealed class AnalogTxAudioSession : IDisposable
{
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly PcmFrameAssembler assembler = new(AnalogVoicePacketCodec.SamplesPerPacket);
    private readonly bool grantDemand;
    private ushort packetSequence;
    private byte frameSequence;
    private bool started;
    private bool ended;
    private bool disposed;

    public AnalogTxAudioSession(
        uint sourceId,
        uint destinationId,
        uint streamId,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        ushort packetSequence = 0,
        byte frameSequence = 0,
        bool grantDemand = false)
    {
        if (sourceId == 0 || sourceId > 0x00FF_FFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId == 0 || destinationId > 0x00FF_FFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));
        if (packetSequence == ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(packetSequence));

        this.sourceId = sourceId;
        this.destinationId = destinationId;
        this.streamId = streamId;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.packetSequence = packetSequence;
        this.frameSequence = frameSequence;
        this.grantDemand = grantDemand;
    }

    public bool IsStarted => started;
    public bool IsEnded => ended;
    public int FramesSent { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            throw new InvalidOperationException("The analog call has already started.");
        if (ended)
            throw new InvalidOperationException("The analog call has already ended.");

        started = true;
    }

    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || ended)
            throw new InvalidOperationException("The analog call must be active before processing audio.");

        int framesBefore = FramesSent;
        assembler.Append(samples, EmitFrame);
        return FramesSent - framesBefore;
    }

    public void End()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
            throw new InvalidOperationException("The analog call has not started.");
        if (ended)
            return;

        var silence = new short[AnalogVoicePacketCodec.SamplesPerPacket];
        byte[] terminator = AnalogVoicePacketCodec.CreatePacket(
            AnalogAudioFrameType.Terminator,
            sourceId,
            destinationId,
            silence,
            frameSequence);
        send(terminator, ushort.MaxValue, streamId);
        ended = true;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        assembler.Reset();
        disposed = true;
    }

    private void EmitFrame(ReadOnlyMemory<short> frame)
    {
        bool first = FramesSent == 0;
        byte[] packet = AnalogVoicePacketCodec.CreatePacket(
            first ? AnalogAudioFrameType.VoiceStart : AnalogAudioFrameType.Voice,
            sourceId,
            destinationId,
            frame.Span,
            frameSequence,
            control: first && grantDemand ? (byte)0x80 : (byte)0);
        send(packet, packetSequence, streamId);
        FramesSent++;
        packetSequence = packetSequence >= ushort.MaxValue - 2 ? (ushort)0 : (ushort)(packetSequence + 1);
        frameSequence = frameSequence >= 253 ? (byte)0 : (byte)(frameSequence + 1);
    }
}
