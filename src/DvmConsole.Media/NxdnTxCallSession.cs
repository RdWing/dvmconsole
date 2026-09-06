// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Represents one explicit NXDN 4800 call: FACCH startup, continuous voice with
// SACCH call signaling, then duplicated FACCH TX_REL on the same FNE stream.
public sealed class NxdnTxCallSession : IDisposable
{
    internal static readonly TimeSpan PacketInterval = TimeSpan.FromMilliseconds(80);
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly bool group;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly ProtocolPacketPacer<NxdnOutboundPacket> packetPacer;
    private readonly NxdnTxAudioSession audio;
    private readonly NxdnPrivacyOptions? privacy;
    private bool started;
    private bool ended;
    private NxdnOutboundPacket? retryTerminator;
    private bool disposed;

    public NxdnTxCallSession(
        uint sourceId,
        uint destinationId,
        bool group,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        NxdnPrivacyOptions? privacy = null)
        : this(
            sourceId,
            destinationId,
            group,
            streamId,
            vocoder,
            send,
            privacy,
            waitForNextPacket: null,
            timeProvider: null)
    {
    }

    internal NxdnTxCallSession(
        uint sourceId,
        uint destinationId,
        bool group,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        Func<CancellationToken, ValueTask> waitForNextPacket,
        NxdnPrivacyOptions? privacy = null)
        : this(
            sourceId,
            destinationId,
            group,
            streamId,
            vocoder,
            send,
            privacy,
            waitForNextPacket ?? throw new ArgumentNullException(nameof(waitForNextPacket)),
            timeProvider: null)
    {
    }

    internal NxdnTxCallSession(
        uint sourceId,
        uint destinationId,
        bool group,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        TimeProvider timeProvider,
        NxdnPrivacyOptions? privacy = null)
        : this(
            sourceId,
            destinationId,
            group,
            streamId,
            vocoder,
            send,
            privacy,
            waitForNextPacket: null,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)))
    {
    }

    private NxdnTxCallSession(
        uint sourceId,
        uint destinationId,
        bool group,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        NxdnPrivacyOptions? privacy,
        Func<CancellationToken, ValueTask>? waitForNextPacket,
        TimeProvider? timeProvider)
    {
        this.sourceId = sourceId;
        this.destinationId = destinationId;
        this.group = group;
        this.streamId = streamId;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.privacy = privacy;
        packetPacer = waitForNextPacket is null
            ? new ProtocolPacketPacer<NxdnOutboundPacket>(PacketInterval, SendPacket, timeProvider)
            : new ProtocolPacketPacer<NxdnOutboundPacket>(waitForNextPacket, SendPacket);
        audio = new NxdnTxAudioSession(
            sourceId,
            destinationId,
            group,
            streamId,
            vocoder,
            QueuePacket,
            privacy: privacy);
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
        QueuePacket(header, audio.PacketSequence, streamId);
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

    public async ValueTask EndAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
            throw new InvalidOperationException("The NXDN call has not started.");
        if (ended)
            return;
        if (retryTerminator is { } pendingTerminator)
        {
            SendPacket(pendingTerminator);
            ended = true;
            return;
        }

        IReadOnlyList<NxdnOutboundPacket> completion = audio.PrepareFrameCompletion();
        byte[] terminator = NxdnVoicePacketCodec.CreateCallControlPacket(
            sourceId, destinationId, group, NxdnVoicePacketCodec.TransmitReleaseMessageType, audio.FrameSequence);
        var finalPacket = new NxdnOutboundPacket(terminator, audio.PacketSequence, streamId);
        retryTerminator = finalPacket;
        audio.AdvanceSequence();
        foreach (NxdnOutboundPacket packet in completion)
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
        => packetPacer.Enqueue(new NxdnOutboundPacket(payload, packetSequence, packetStreamId));

    private void SendPacket(NxdnOutboundPacket packet)
        => send(packet.Payload, packet.Sequence, packet.StreamId);
}
