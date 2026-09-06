// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
    private readonly ProtocolPacketPacer<P25OutboundPacket> packetPacer;
    private readonly P25TxAudioSession audio;
    private bool started;
    private bool ended;
    private P25OutboundPacket? retryTerminator;
    private bool disposed;

    public P25TxCallSession(
        uint sourceId,
        uint destinationId,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        P25TxEncryptionOptions? encryption = null)
        : this(
            sourceId,
            destinationId,
            streamId,
            vocoder,
            send,
            encryption,
            waitForNextPacket: null,
            timeProvider: null)
    {
    }

    internal P25TxCallSession(
        uint sourceId,
        uint destinationId,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        Func<CancellationToken, ValueTask> waitForNextPacket,
        P25TxEncryptionOptions? encryption = null)
        : this(
            sourceId,
            destinationId,
            streamId,
            vocoder,
            send,
            encryption,
            waitForNextPacket ?? throw new ArgumentNullException(nameof(waitForNextPacket)),
            timeProvider: null)
    {
    }

    internal P25TxCallSession(
        uint sourceId,
        uint destinationId,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        TimeProvider timeProvider,
        P25TxEncryptionOptions? encryption = null)
        : this(
            sourceId,
            destinationId,
            streamId,
            vocoder,
            send,
            encryption,
            waitForNextPacket: null,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)))
    {
    }

    private P25TxCallSession(
        uint sourceId,
        uint destinationId,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        P25TxEncryptionOptions? encryption,
        Func<CancellationToken, ValueTask>? waitForNextPacket,
        TimeProvider? timeProvider)
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
        packetPacer = waitForNextPacket is null
            ? new ProtocolPacketPacer<P25OutboundPacket>(LduInterval, SendPacket, timeProvider)
            : new ProtocolPacketPacer<P25OutboundPacket>(waitForNextPacket, SendPacket);
        audio = new P25TxAudioSession(
            sourceId,
            destinationId,
            streamId,
            vocoder ?? throw new ArgumentNullException(nameof(vocoder)),
            QueuePacket,
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

        QueuePacket(
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

    public async ValueTask EndAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
            throw new InvalidOperationException("The P25 call has not started.");
        if (ended)
            return;

        if (retryTerminator is { } pendingTerminator)
        {
            SendPacket(pendingTerminator);
            ended = true;
            return;
        }

        IReadOnlyList<P25OutboundPacket> completion = audio.PrepareLduCompletion();
        byte[] terminator = P25DfsiFrameCodec.CreateTduPayload(sourceId, destinationId, grantDemand: false);
        var finalPacket = new P25OutboundPacket(
            terminator,
            P25DfsiFrameCodec.RtpCallEndSequence,
            streamId);
        retryTerminator = finalPacket;
        foreach (P25OutboundPacket packet in completion)
            packetPacer.Enqueue(packet);
        packetPacer.Enqueue(finalPacket);
        await packetPacer.CompleteAsync(cancellationToken).ConfigureAwait(false);

        ended = true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        packetPacer.Dispose();
        audio.Dispose();
        disposed = true;
    }

    private void QueuePacket(ReadOnlyMemory<byte> payload, ushort packetSequence, uint packetStreamId)
        => packetPacer.Enqueue(new P25OutboundPacket(payload, packetSequence, packetStreamId));

    private void SendPacket(P25OutboundPacket packet)
        => send(packet.Payload, packet.Sequence, packet.StreamId);
}
