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
