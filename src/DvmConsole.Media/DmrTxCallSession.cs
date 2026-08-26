using fnecore.DMR;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Represents one explicit DMR group call. Start emits the voice LC header,
// Process emits encoded voice packets, and End emits the LC terminator.
// Microphone capture and FNE connection state remain owned by the host.
public sealed class DmrTxCallSession : IDisposable
{
    private static readonly TimeSpan PacketInterval = TimeSpan.FromMilliseconds(60);
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly byte slot;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly DmrTxPacketSequence sequence;
    private readonly DmrTxAudioSession audio;
    private readonly DmrPrivacyOptions? privacy;
    private bool started;
    private bool ended;
    private bool disposed;

    public DmrTxCallSession(
        uint sourceId,
        uint destinationId,
        byte slot,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        ushort packetSequence = 0,
        byte frameSequence = 0,
        DmrPrivacyOptions? privacy = null)
    {
        if (sourceId == 0 || sourceId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId == 0 || destinationId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        this.sourceId = sourceId;
        this.destinationId = destinationId;
        this.slot = slot;
        this.streamId = streamId;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.privacy = privacy;
        sequence = new DmrTxPacketSequence(packetSequence, frameSequence);

        var lc = new LC
        {
            FLCO = (byte)DMRFLCO.FLCO_GROUP,
            // DMR Association privacy marks both the Group Voice Channel User
            // LC and the separate privacy-indicator LC with its feature-set ID.
            // The encrypted service option remains an independent LC field.
            FID = privacy is null ? (byte)0 : DmrPrivacyAlgorithms.FeatureId,
            Encrypted = privacy is not null,
            SrcId = sourceId,
            DstId = destinationId
        };
        var embedded = new EmbeddedData();
        embedded.SetLC(lc);
        audio = new DmrTxAudioSession(
            sourceId,
            destinationId,
            slot,
            streamId,
            vocoder ?? throw new ArgumentNullException(nameof(vocoder)),
            send,
            sequence,
            // A DMR voice LC header is followed by a voice-sync burst. The
            // legacy transmitter starts its N sequence at zero here; starting
            // at one produces an embedded-data burst before the FNE has seen
            // voice sync and causes some masters to drop the call.
            embeddedSequence: 0,
            embeddedData: embedded,
            privacy: privacy);
    }

    public bool IsStarted => started;
    public bool IsEnded => ended;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            throw new InvalidOperationException("The DMR call has already started.");
        if (ended)
            throw new InvalidOperationException("The DMR call has already ended.");

        byte[] header = DmrVoicePacketCodec.CreateVoiceLcHeaderPacket(
            sourceId,
            destinationId,
            slot,
            sequence.FrameSequence,
            encrypted: privacy is not null);
        send(header, sequence.PacketSequence, streamId);
        sequence.Advance();
        if (privacy is not null)
        {
            byte[] privacyHeader = DmrVoicePacketCodec.CreatePrivacyIndicatorPacket(
                sourceId,
                destinationId,
                slot,
                sequence.FrameSequence,
                privacy);
            send(privacyHeader, sequence.PacketSequence, streamId);
            sequence.Advance();
        }
        started = true;
    }

    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || ended)
            throw new InvalidOperationException("The DMR call must be active before processing audio.");
        return audio.Process(samples);
    }

    public void End()
        => EndAsync(static _ => ValueTask.CompletedTask, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public ValueTask EndAsync(CancellationToken cancellationToken = default)
        => EndAsync(WaitForNextPacketAsync, cancellationToken);

    internal async ValueTask EndAsync(
        Func<CancellationToken, ValueTask> waitForNextPacket,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(waitForNextPacket);
        if (!started)
            throw new InvalidOperationException("The DMR call has not started.");
        if (ended)
            return;

        IReadOnlyList<DmrOutboundPacket> completion = audio.PrepareSuperframeCompletion();
        foreach (DmrOutboundPacket packet in completion)
        {
            await waitForNextPacket(cancellationToken).ConfigureAwait(false);
            send(packet.Payload, packet.Sequence, packet.StreamId);
        }

        await waitForNextPacket(cancellationToken).ConfigureAwait(false);
        byte[] terminator = DmrVoicePacketCodec.CreateTerminatorPacket(
            sourceId,
            destinationId,
            slot,
            sequence.FrameSequence,
            encrypted: privacy is not null);
        send(terminator, sequence.PacketSequence, streamId);
        sequence.Advance();
        ended = true;
    }

    private static async ValueTask WaitForNextPacketAsync(CancellationToken cancellationToken)
        => await Task.Delay(PacketInterval, cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        if (disposed)
            return;
        audio.Dispose();
        disposed = true;
    }
}
