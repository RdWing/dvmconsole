using System.Diagnostics;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceivePacketJitterBufferTests
{
    [Fact]
    public void AStreamKeepsTheTargetThatItsFirstPacketSelected()
    {
        var buffer = new ReceivePacketJitterBuffer<Packet>(
            packet => packet.StreamId,
            packet => packet.Sequence,
            _ => false,
            packet => packet.Profile);
        long start = Stopwatch.GetTimestamp();
        ReceiveJitterBufferProfile shortTarget = CreateProfile(60);
        ReceiveJitterBufferProfile longerTarget = CreateProfile(180);

        buffer.Enqueue(new Packet(1, 10, shortTarget), start);
        buffer.Enqueue(new Packet(1, 11, longerTarget), Add(start, 10));

        Assert.True(buffer.TryDequeue(
            Add(start, 70),
            drain: false,
            out Packet first,
            out _,
            out ReceiveJitterBufferDequeueMetadata firstMetadata));
        Assert.Equal((ushort)10, first.Sequence);
        Assert.Equal(TimeSpan.FromMilliseconds(60), firstMetadata.TargetDelay);

        Assert.True(buffer.TryDequeue(
            Add(start, 130),
            drain: false,
            out Packet second,
            out _,
            out ReceiveJitterBufferDequeueMetadata secondMetadata));
        Assert.Equal((ushort)11, second.Sequence);
        Assert.Equal(TimeSpan.FromMilliseconds(60), secondMetadata.TargetDelay);

        buffer.Enqueue(new Packet(2, 20, longerTarget), Add(start, 140));
        Assert.False(buffer.TryDequeue(
            Add(start, 210),
            drain: false,
            out _,
            out _,
            out _));

        Assert.True(buffer.TryDequeue(
            Add(start, 330),
            drain: false,
            out _,
            out _,
            out ReceiveJitterBufferDequeueMetadata thirdMetadata));
        Assert.Equal(TimeSpan.FromMilliseconds(180), thirdMetadata.TargetDelay);
    }

    [Fact]
    public void OrderedMetadataDoesNotConsumeAVoicePlayoutInterval()
    {
        ReceiveJitterBufferProfile profile = CreateProfile(120);
        var buffer = new ReceivePacketJitterBuffer<ClassifiedPacket>(
            packet => packet.StreamId,
            packet => packet.Sequence,
            packet => packet.Kind,
            packet => packet.Profile);
        long start = Stopwatch.GetTimestamp();

        buffer.Enqueue(new ClassifiedPacket(1, 0, ReceiveJitterPacketKind.Metadata, profile), start);
        Assert.True(buffer.TryDequeue(start, false, out ClassifiedPacket startMetadata, out _, out _));
        Assert.Equal(ReceiveJitterPacketKind.Metadata, startMetadata.Kind);

        buffer.Enqueue(new ClassifiedPacket(1, 1, ReceiveJitterPacketKind.Voice, profile), Add(start, 10));
        Assert.False(buffer.TryDequeue(Add(start, 120), false, out _, out _, out _));
        Assert.True(buffer.TryDequeue(Add(start, 130), false, out ClassifiedPacket voice, out _, out _));
        Assert.Equal(ReceiveJitterPacketKind.Voice, voice.Kind);

        buffer.Enqueue(new ClassifiedPacket(1, 2, ReceiveJitterPacketKind.Metadata, profile), Add(start, 131));
        Assert.True(buffer.TryDequeue(Add(start, 131), false, out ClassifiedPacket laterMetadata, out _, out _));
        Assert.Equal(ReceiveJitterPacketKind.Metadata, laterMetadata.Kind);
    }

    [Fact]
    public void TerminatorStartsAPacedFlushInsteadOfReleasingAllVoiceAtOnce()
    {
        ReceiveJitterBufferProfile profile = CreateProfile(180);
        var buffer = new ReceivePacketJitterBuffer<ClassifiedPacket>(
            packet => packet.StreamId,
            packet => packet.Sequence,
            packet => packet.Kind,
            packet => packet.Profile);
        long start = Stopwatch.GetTimestamp();
        long terminatorArrival = Add(start, 2);

        buffer.Enqueue(new ClassifiedPacket(
            1, 10, ReceiveJitterPacketKind.Voice, profile), start);
        buffer.Enqueue(new ClassifiedPacket(
            1, 11, ReceiveJitterPacketKind.Voice, profile), Add(start, 1));
        buffer.Enqueue(new ClassifiedPacket(
            1, 12, ReceiveJitterPacketKind.Terminator, profile), terminatorArrival);

        Assert.True(buffer.TryDequeue(
            terminatorArrival,
            drain: false,
            out ClassifiedPacket firstVoice,
            out _,
            out _));
        Assert.Equal((ushort)10, firstVoice.Sequence);

        Assert.False(buffer.TryDequeue(
            terminatorArrival,
            drain: false,
            out _,
            out TimeSpan waitTime,
            out _));
        Assert.InRange(
            waitTime,
            TimeSpan.FromMilliseconds(59),
            TimeSpan.FromMilliseconds(61));

        long secondDeadline = Add(terminatorArrival, 60);
        Assert.True(buffer.TryDequeue(
            secondDeadline,
            drain: false,
            out ClassifiedPacket secondVoice,
            out _,
            out _));
        Assert.Equal((ushort)11, secondVoice.Sequence);

        Assert.True(buffer.TryDequeue(
            secondDeadline,
            drain: false,
            out ClassifiedPacket terminator,
            out _,
            out _));
        Assert.Equal(ReceiveJitterPacketKind.Terminator, terminator.Kind);
    }

    [Fact]
    public void TerminatorWithoutBufferedVoiceRemainsImmediatelyReady()
    {
        ReceiveJitterBufferProfile profile = CreateProfile(180);
        var buffer = new ReceivePacketJitterBuffer<ClassifiedPacket>(
            packet => packet.StreamId,
            packet => packet.Sequence,
            packet => packet.Kind,
            packet => packet.Profile);
        long start = Stopwatch.GetTimestamp();

        buffer.Enqueue(new ClassifiedPacket(
            1, 10, ReceiveJitterPacketKind.Terminator, profile), start);

        Assert.True(buffer.TryDequeue(
            start,
            drain: false,
            out ClassifiedPacket terminator,
            out _,
            out _));
        Assert.Equal(ReceiveJitterPacketKind.Terminator, terminator.Kind);
    }

    [Fact]
    public void StreamPresenceTracksDequeuesAndExplicitRemoval()
    {
        ReceiveJitterBufferProfile profile = CreateProfile(0);
        var buffer = new ReceivePacketJitterBuffer<ClassifiedPacket>(
            packet => packet.StreamId,
            packet => packet.Sequence,
            packet => packet.Kind,
            packet => packet.Profile);
        long start = Stopwatch.GetTimestamp();

        buffer.Enqueue(new ClassifiedPacket(
            1, 10, ReceiveJitterPacketKind.Voice, profile), start);
        buffer.Enqueue(new ClassifiedPacket(
            2, 20, ReceiveJitterPacketKind.Voice, profile), start);

        Assert.True(buffer.ContainsStream(1));
        Assert.True(buffer.ContainsStream(2));
        Assert.True(buffer.TryDequeue(start, false, out ClassifiedPacket first, out _, out _));
        Assert.Equal((uint)1, first.StreamId);
        Assert.False(buffer.ContainsStream(1));
        Assert.True(buffer.ContainsStream(2));

        Assert.True(buffer.TryRemoveOldest(packet => packet.StreamId == 2));
        Assert.False(buffer.ContainsStream(2));
    }

    private static ReceiveJitterBufferProfile CreateProfile(int milliseconds)
        => new(
            TimeSpan.FromMilliseconds(60),
            TimeSpan.FromMilliseconds(milliseconds),
            IsAdaptive: true);

    private static long Add(long timestamp, int milliseconds)
        => timestamp + (long)Math.Round(
            TimeSpan.FromMilliseconds(milliseconds).TotalSeconds * Stopwatch.Frequency);

    private readonly record struct Packet(
        uint StreamId,
        ushort Sequence,
        ReceiveJitterBufferProfile Profile);

    private readonly record struct ClassifiedPacket(
        uint StreamId,
        ushort Sequence,
        ReceiveJitterPacketKind Kind,
        ReceiveJitterBufferProfile Profile);
}
