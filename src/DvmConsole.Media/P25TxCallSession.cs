using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Represents one explicit P25 group call. Start emits the legacy grant-demand
// TDU, Process emits LDU1/LDU2 voice payloads, and End emits one TDU
// terminator, matching the fnecore call lifecycle. Optional encryption
// uses the same HDU/LDU2 metadata and key-stream boundary as the legacy host.
public sealed class P25TxCallSession : IDisposable
{
    private static readonly TimeSpan LduInterval = TimeSpan.FromMilliseconds(180);
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly P25TxAudioSession audio;
    private bool started;
    private bool ended;
    private bool disposed;

    public P25TxCallSession(
        uint sourceId,
        uint destinationId,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        P25TxEncryptionOptions? encryption = null)
    {
        if (sourceId == 0 || sourceId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId == 0 || destinationId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        this.sourceId = sourceId;
        this.destinationId = destinationId;
        this.streamId = streamId;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        audio = new P25TxAudioSession(
            sourceId,
            destinationId,
            streamId,
            vocoder ?? throw new ArgumentNullException(nameof(vocoder)),
            send,
            encryption: encryption);
    }

    public bool IsStarted => started;
    public bool IsEnded => ended;
    public int CodewordsEncoded => audio.CodewordsEncoded;
    public int LdusSent => audio.LdusSent;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            throw new InvalidOperationException("The P25 call has already started.");
        if (ended)
            throw new InvalidOperationException("The P25 call has already ended.");

        send(
            P25DfsiFrameCodec.CreateTduPayload(sourceId, destinationId, grantDemand: true),
            P25DfsiFrameCodec.RtpCallEndSequence,
            streamId);
        started = true;
    }

    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || ended)
            throw new InvalidOperationException("The P25 call must be active before processing audio.");
        return audio.Process(samples);
    }

    public int ProcessSingleTone(double frequencyHz)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || ended)
            throw new InvalidOperationException("The P25 call must be active before processing audio.");
        return audio.ProcessSingleTone(frequencyHz);
    }

    public ValueTask EndAsync(CancellationToken cancellationToken = default)
        => EndAsync(WaitForNextLduAsync, cancellationToken);

    internal async ValueTask EndAsync(
        Func<CancellationToken, ValueTask> waitForNextLdu,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(waitForNextLdu);
        if (!started)
            throw new InvalidOperationException("The P25 call has not started.");
        if (ended)
            return;

        IReadOnlyList<P25OutboundPacket> completion = audio.PrepareLduCompletion();
        foreach (P25OutboundPacket packet in completion)
        {
            await waitForNextLdu(cancellationToken).ConfigureAwait(false);
            send(packet.Payload, packet.Sequence, packet.StreamId);
        }
        await waitForNextLdu(cancellationToken).ConfigureAwait(false);
        byte[] terminator = P25DfsiFrameCodec.CreateTduPayload(sourceId, destinationId, grantDemand: false);
        send(terminator, P25DfsiFrameCodec.RtpCallEndSequence, streamId);

        ended = true;
    }

    private static async ValueTask WaitForNextLduAsync(CancellationToken cancellationToken)
        => await Task.Delay(LduInterval, cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        if (disposed)
            return;
        audio.Dispose();
        disposed = true;
    }
}
