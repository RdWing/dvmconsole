using System.Text;

namespace DvmConsole.Media;

public enum AnalogAudioFrameType : byte
{
    VoiceStart = 0x00,
    Voice = 0x01,
    Terminator = 0x02
}

/// <summary>
/// Encodes and extracts the G.711 μ-law audio carried by an FNE analog packet.
/// The wire length follows the published fnecore analog packet constant. The
/// packet's first 160 bytes of audio carry the μ-law samples.
/// </summary>
public static class AnalogVoicePacketCodec
{
    public const int HeaderBytes = 20;
    public const int SamplesPerPacket = 160;
    public const int EncodedAudioBytes = SamplesPerPacket;
    public const int TrailerBytes = 4;
    public const int PacketBytes = (int)fnecore.Constants.AnalogPacketLength;
    public const int AudioBytes = PacketBytes - HeaderBytes - TrailerBytes;
    public const string PacketTag = "ANOD";
    public const int SourceIdOffset = 5;
    public const int DestinationIdOffset = 8;
    public const int ControlOffset = 14;
    public const int FrameTypeOffset = 15;
    public const int AudioOffset = HeaderBytes;

    public static bool TryExtractPcm(ReadOnlySpan<byte> packet, Span<short> samples)
    {
        if (packet.Length < PacketBytes || samples.Length < SamplesPerPacket)
            return false;

        ReadOnlySpan<byte> audio = packet.Slice(AudioOffset, EncodedAudioBytes);
        for (int index = 0; index < SamplesPerPacket; index++)
            samples[index] = DecodeMuLaw(audio[index]);
        return true;
    }

    public static short[] ExtractPcm(ReadOnlySpan<byte> packet)
    {
        var samples = new short[SamplesPerPacket];
        if (!TryExtractPcm(packet, samples))
            throw new ArgumentException("The analog packet does not contain a complete μ-law frame.", nameof(packet));
        return samples;
    }

    public static byte[] CreatePacket(
        AnalogAudioFrameType frameType,
        uint sourceId,
        uint destinationId,
        ReadOnlySpan<short> samples,
        byte sequenceNumber = 0,
        byte control = 0,
        bool individual = false)
    {
        if (sourceId == 0 || sourceId > 0x00FF_FFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId == 0 || destinationId > 0x00FF_FFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (samples.Length < SamplesPerPacket)
            throw new ArgumentException("An analog packet requires one 20 ms PCM frame.", nameof(samples));

        var packet = new byte[PacketBytes];
        Encoding.ASCII.GetBytes(PacketTag, packet.AsSpan(0, PacketTag.Length));
        packet[4] = sequenceNumber;
        WriteUInt24(packet.AsSpan(SourceIdOffset, 3), sourceId);
        WriteUInt24(packet.AsSpan(DestinationIdOffset, 3), destinationId);
        packet[ControlOffset] = control;
        packet[FrameTypeOffset] = (byte)frameType;
        if (individual)
            packet[FrameTypeOffset] |= 0x40;

        for (int index = 0; index < SamplesPerPacket; index++)
            packet[AudioOffset + index] = EncodeMuLaw(samples[index]);

        return packet;
    }

    public static byte EncodeMuLaw(short sample)
    {
        int pcm = sample >> 2;
        int mask;
        if (pcm < 0)
        {
            pcm = -pcm;
            mask = 0x7F;
        }
        else
        {
            mask = 0xFF;
        }

        if (pcm > 8159)
            pcm = 8159;
        pcm += 0x21;

        int segment = SearchSegment(pcm);
        if (segment >= 8)
            return (byte)(0x7F ^ mask);

        int encoded = (segment << 4) | ((pcm >> (segment + 1)) & 0x0F);
        return (byte)(encoded ^ mask);
    }

    public static short DecodeMuLaw(byte encoded)
    {
        int ulaw = (~encoded) & 0xFF;
        int sample = ((ulaw & 0x0F) << 3) + 0x84;
        sample <<= (ulaw & 0x70) >> 4;
        sample = (ulaw & 0x80) != 0 ? 0x84 - sample : sample - 0x84;
        return (short)sample;
    }

    private static int SearchSegment(int value)
    {
        ReadOnlySpan<int> segmentEnds = [0x3F, 0x7F, 0xFF, 0x1FF, 0x3FF, 0x7FF, 0xFFF, 0x1FFF];
        for (int index = 0; index < segmentEnds.Length; index++)
        {
            if (value <= segmentEnds[index])
                return index;
        }

        return segmentEnds.Length;
    }

    private static void WriteUInt24(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 16);
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)value;
    }
}
