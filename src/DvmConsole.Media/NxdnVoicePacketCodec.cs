namespace DvmConsole.Media;

// Maps the 4800-baud NXDN voice channel carried by an FNE NXDD packet. NXDN
// 9600/EFR is not implemented in dvmhost.
public static class NxdnVoicePacketCodec
{
    // Current dvmhost uses the common 24-byte FNE message header, followed by
    // the two modem tag bytes and the 48-byte scrambled NXDN RF frame. Older
    // peers used a 20-byte header with the RF frame directly at offset 20.
    public const int LegacyHeaderBytes = 20;
    public const int HeaderBytes = 24;
    public const int ModemPrefixBytes = 2;
    public const int FrameOffset = HeaderBytes + ModemPrefixBytes;
    public const int FrameBytes = 48;
    public const int DeclaredPacketBytes = FrameOffset + FrameBytes;
    public const int PacketPadBytes = 4;
    public const int PacketBytes = DeclaredPacketBytes + PacketPadBytes;
    public const int LegacyPacketBytes = LegacyHeaderBytes + FrameBytes + 2;
    public const int CodewordBytes = 9;
    public const int CodewordsPerFrame = 4;
    public const int AmbeBytes = CodewordBytes * CodewordsPerFrame;
    public const ushort RtpCallEndSequence = ushort.MaxValue;

    public const byte VoiceCallMessageType = 0x01;
    public const byte VoiceCallIvMessageType = 0x03;
    public const byte TransmitReleaseMessageType = 0x08;

    private const int VoiceOffsetBytes = 12;
    private const int SacchOffsetBits = 36;
    private const int Facch1OffsetBits = 96;
    private const int Facch1Bits = 144;

    private static readonly byte[] Scrambler =
    [
        0x00, 0x00, 0x00, 0x82, 0xA0, 0x88, 0x8A, 0x00,
        0xA2, 0xA8, 0x82, 0x8A, 0x82, 0x02, 0x20, 0x08,
        0x8A, 0x20, 0xAA, 0xA2, 0x82, 0x08, 0x22, 0x8A,
        0xAA, 0x08, 0x28, 0x88, 0x28, 0x28, 0x00, 0x0A,
        0x02, 0x82, 0x20, 0x28, 0x82, 0x2A, 0xAA, 0x20,
        0x22, 0x80, 0xA8, 0x8A, 0x08, 0xA0, 0xAA, 0x02
    ];

    private static readonly int[] SacchInterleave =
    [
        0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55,
        1, 6, 11, 16, 21, 26, 31, 36, 41, 46, 51, 56,
        2, 7, 12, 17, 22, 27, 32, 37, 42, 47, 52, 57,
        3, 8, 13, 18, 23, 28, 33, 38, 43, 48, 53, 58,
        4, 9, 14, 19, 24, 29, 34, 39, 44, 49, 54, 59
    ];

    private static readonly HashSet<int> SacchPunctures =
        [5, 11, 17, 23, 29, 35, 41, 47, 53, 59, 65, 71];

    private static readonly int[] FacchInterleave = Enumerable.Range(0, 9)
        .SelectMany(column => Enumerable.Range(0, 16).Select(row => column + (row * 9)))
        .ToArray();

    private static readonly HashSet<int> FacchPunctures =
        Enumerable.Range(0, 48).Select(index => 1 + (index * 4)).ToHashSet();

    public readonly record struct CallMetadata(
        byte MessageType,
        ushort SourceId,
        ushort DestinationId,
        bool Group,
        byte CipherType,
        byte KeyId,
        byte[] MessageIndicator);

    public static bool TryExtractFrame(ReadOnlySpan<byte> packet, Span<byte> frame)
    {
        if (packet.Length < LegacyPacketBytes || frame.Length < FrameBytes ||
            packet[0] != (byte)'N' || packet[1] != (byte)'X' ||
            packet[2] != (byte)'D' || packet[3] != (byte)'D')
        {
            return false;
        }

        // Prefer the current dvmhost layout, then the documented header-only
        // variant and finally the legacy 20-byte layout. Validate the
        // descrambled sync and LICH so padding cannot be mistaken for a frame.
        return TryExtractFrameAt(packet, FrameOffset, frame) ||
            TryExtractFrameAt(packet, HeaderBytes, frame) ||
            TryExtractFrameAt(packet, LegacyHeaderBytes, frame);
    }

    public static byte[] ExtractFrame(ReadOnlySpan<byte> packet)
    {
        byte[] frame = new byte[FrameBytes];
        if (!TryExtractFrame(packet, frame))
            throw new ArgumentException("The NXDN packet does not contain a complete 48-byte frame.", nameof(packet));
        return frame;
    }

    public static bool TryExtractAmbe(ReadOnlySpan<byte> packet, Span<byte> ambe, out int codewordCount)
    {
        codewordCount = 0;
        if (ambe.Length < AmbeBytes)
            return false;

        Span<byte> frame = stackalloc byte[FrameBytes];
        if (!TryExtractFrame(packet, frame))
            return false;
        Scramble(frame);
        if (frame[0] != 0xCD || frame[1] != 0xF5 || (frame[2] & 0xF0) != 0x90)
            return false;

        byte lich = DecodeLich(frame);
        if (!HasValidLichParity(lich) || ((lich >> 6) & 0x03) != 0x02)
            return false;

        byte option = (byte)((lich >> 2) & 0x03);
        int sourceOffset = option switch
        {
            0x01 => VoiceOffsetBytes + (CodewordBytes * 2),
            0x02 => VoiceOffsetBytes,
            0x03 => VoiceOffsetBytes,
            _ => -1
        };
        codewordCount = option == 0x03 ? 4 : option is 0x01 or 0x02 ? 2 : 0;
        if (sourceOffset < 0 || codewordCount == 0)
            return false;

        frame.Slice(sourceOffset, codewordCount * CodewordBytes).CopyTo(ambe);
        return true;
    }

    public static byte[] CreateVoicePacket(
        uint sourceId,
        uint destinationId,
        bool group,
        byte frameSequence,
        ReadOnlySpan<byte> ambe,
        byte ran = 0,
        byte superframePart = 0,
        byte cipherType = 0,
        byte keyId = 0,
        CallMetadata? sacchMetadata = null)
    {
        ValidateIds(sourceId, destinationId);
        if (ambe.Length < AmbeBytes)
            throw new ArgumentException($"NXDN voice data must contain {AmbeBytes} bytes.", nameof(ambe));
        if (ran > 63)
            throw new ArgumentOutOfRangeException(nameof(ran));
        if (superframePart > 3)
            throw new ArgumentOutOfRangeException(nameof(superframePart));
        if (cipherType > 3)
            throw new ArgumentOutOfRangeException(nameof(cipherType));
        if (keyId > 63)
            throw new ArgumentOutOfRangeException(nameof(keyId));

        byte[] packet = CreatePacketHeader(sourceId, destinationId, group, VoiceCallMessageType, frameSequence);
        Span<byte> frame = packet.AsSpan(FrameOffset, FrameBytes);
        AddSync(frame);
        EncodeLich(frame, functionChannelType: 2, option: 3);
        Span<byte> linkControl = stackalloc byte[10];
        WriteCallMetadata(
            linkControl,
            sacchMetadata ?? CreateVoiceCallMetadata(sourceId, destinationId, group, cipherType, keyId));
        Span<byte> sacchData = stackalloc byte[3];
        sacchData.Clear();
        int sacchBitOffset = superframePart * 18;
        for (int bit = 0; bit < 18; bit++)
            SetBit(sacchData, bit, GetBit(linkControl, sacchBitOffset + bit));
        EncodeSacch(frame, ran, (byte)(3 - superframePart), sacchData);
        ambe[..AmbeBytes].CopyTo(frame[VoiceOffsetBytes..]);
        Scramble(frame);
        return packet;
    }

    // DES and AES call startup uses one FACCH frame: VCALL in the first half
    // and VCALL_IV in the second. Keeping both messages in one RF frame gives
    // receivers the cipher selection and current IV before the first voice
    // frame without inserting a second control-frame delay.
    public static byte[] CreatePrivacyCallStartPacket(
        uint sourceId,
        uint destinationId,
        bool group,
        byte frameSequence,
        byte cipherType,
        byte keyId,
        ReadOnlySpan<byte> messageIndicator,
        byte ran = 0)
    {
        ValidateIds(sourceId, destinationId);
        if (cipherType is not (NxdnPrivacyAlgorithms.Des or NxdnPrivacyAlgorithms.Aes256))
            throw new ArgumentOutOfRangeException(nameof(cipherType));
        if (keyId > 63)
            throw new ArgumentOutOfRangeException(nameof(keyId));
        if (messageIndicator.Length < NxdnPrivacyAlgorithms.MessageIndicatorBytes)
        {
            throw new ArgumentException(
                "NXDN DES/AES privacy requires an 8-byte message indicator.",
                nameof(messageIndicator));
        }
        if (ran > 63)
            throw new ArgumentOutOfRangeException(nameof(ran));

        byte[] packet = CreatePacketHeader(
            sourceId,
            destinationId,
            group,
            VoiceCallMessageType,
            frameSequence);
        Span<byte> frame = packet.AsSpan(FrameOffset, FrameBytes);
        AddSync(frame);
        EncodeLich(frame, functionChannelType: 0, option: 0);
        EncodeSacch(frame, ran, structure: 0, [0x10, 0x00, 0x00]);

        Span<byte> voiceCall = stackalloc byte[10];
        WriteCallMetadata(
            voiceCall,
            CreateVoiceCallMetadata(sourceId, destinationId, group, cipherType, keyId));
        Span<byte> voiceCallIv = stackalloc byte[10];
        WriteCallMetadata(
            voiceCallIv,
            CreateVoiceCallIvMetadata(messageIndicator));
        EncodeFacch1(frame, Facch1OffsetBits, voiceCall);
        EncodeFacch1(frame, Facch1OffsetBits + Facch1Bits, voiceCallIv);
        Scramble(frame);
        return packet;
    }

    public static byte[] CreateCallControlPacket(
        uint sourceId,
        uint destinationId,
        bool group,
        byte messageType,
        byte frameSequence,
        byte cipherType = 0,
        byte keyId = 0,
        ReadOnlySpan<byte> messageIndicator = default,
        byte ran = 0)
    {
        ValidateIds(sourceId, destinationId);
        if (messageType is not (VoiceCallMessageType or VoiceCallIvMessageType or TransmitReleaseMessageType))
            throw new ArgumentOutOfRangeException(nameof(messageType));
        if (cipherType > 3)
            throw new ArgumentOutOfRangeException(nameof(cipherType));
        if (keyId > 63)
            throw new ArgumentOutOfRangeException(nameof(keyId));
        if (ran > 63)
            throw new ArgumentOutOfRangeException(nameof(ran));

        byte[] packet = CreatePacketHeader(sourceId, destinationId, group, messageType, frameSequence);
        Span<byte> frame = packet.AsSpan(FrameOffset, FrameBytes);
        AddSync(frame);
        EncodeLich(frame, functionChannelType: 0, option: 0);
        EncodeSacch(frame, ran, structure: 0, [0x10, 0x00, 0x00]);

        CallMetadata metadata = messageType == VoiceCallIvMessageType
            ? CreateVoiceCallIvMetadata(messageIndicator)
            : CreateVoiceCallMetadata(sourceId, destinationId, group, cipherType, keyId) with
            {
                MessageType = messageType
            };
        Span<byte> linkControl = stackalloc byte[10];
        WriteCallMetadata(linkControl, metadata);

        EncodeFacch1(frame, Facch1OffsetBits, linkControl);
        EncodeFacch1(frame, Facch1OffsetBits + Facch1Bits, linkControl);
        Scramble(frame);
        return packet;
    }

    public static bool TryExtractCallMetadata(ReadOnlySpan<byte> packet, out CallMetadata metadata)
    {
        metadata = default;
        return TryExtractFacchCallMetadata(packet, 0, out metadata) ||
            TryExtractFacchCallMetadata(packet, 1, out metadata);
    }

    public static bool TryExtractFacchCallMetadata(
        ReadOnlySpan<byte> packet,
        int facchIndex,
        out CallMetadata metadata)
    {
        metadata = default;
        if (facchIndex is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(facchIndex));

        Span<byte> frame = stackalloc byte[FrameBytes];
        if (!TryExtractFrame(packet, frame))
            return false;
        Scramble(frame);
        byte lich = DecodeLich(frame);
        // FACCH call-control frames use the non-steal control-channel LICH.
        // Voice frames carry AMBE in the same bit region; attempting FACCH
        // correction there can turn valid voice patterns into phantom VCALL
        // metadata and silently suppress their audio.
        if (((lich >> 4) & 0x03) != 0)
            return false;
        int offset = Facch1OffsetBits + (facchIndex * Facch1Bits);
        if (!TryDecodeFacch1(frame, offset, out byte[] linkControl))
            return false;

        return TryParseCallMetadata(linkControl, out metadata);
    }

    public static bool TryExtractSacchFragment(
        ReadOnlySpan<byte> packet,
        out byte structure,
        Span<byte> payload)
    {
        structure = 0;
        if (payload.Length < 3)
            return false;

        Span<byte> frame = stackalloc byte[FrameBytes];
        if (!TryExtractFrame(packet, frame))
            return false;
        Scramble(frame);
        byte lich = DecodeLich(frame);
        if (!HasValidLichParity(lich) || ((lich >> 4) & 0x03) != 2)
            return false;

        Span<int> symbols = stackalloc int[72];
        symbols.Fill(-1);
        int input = 0;
        for (int index = 0; index < symbols.Length; index++)
        {
            if (SacchPunctures.Contains(index))
                continue;
            symbols[index] = GetBit(frame, SacchOffsetBits + SacchInterleave[input++]) ? 1 : 0;
        }

        Span<byte> decoded = stackalloc byte[5];
        if (!TryDecodeConvolution(symbols, decoded, decodedBitCount: 36) ||
            CreateCrc(decoded, 26, 6, 0x27, 0x3F) != ReadBits(decoded, 26, 6))
        {
            return false;
        }

        structure = (byte)(decoded[0] >> 6);
        payload[..3].Clear();
        for (int bit = 0; bit < 18; bit++)
            SetBit(payload, bit, GetBit(decoded, 8 + bit));
        return true;
    }

    public static bool TryParseCallMetadata(
        ReadOnlySpan<byte> linkControl,
        out CallMetadata metadata)
    {
        metadata = default;
        if (linkControl.Length < 9)
            return false;

        byte type = (byte)(linkControl[0] & 0x3F);
        if (type == VoiceCallIvMessageType)
        {
            metadata = new CallMetadata(type, 0, 0, true, 0, 0, linkControl[1..9].ToArray());
            return true;
        }
        if (type is not (VoiceCallMessageType or TransmitReleaseMessageType))
            return false;

        byte callType = (byte)((linkControl[2] >> 5) & 0x07);
        metadata = new CallMetadata(
            type,
            (ushort)((linkControl[3] << 8) | linkControl[4]),
            (ushort)((linkControl[5] << 8) | linkControl[6]),
            callType != 4,
            type == VoiceCallMessageType ? (byte)(linkControl[7] >> 6) : (byte)0,
            type == VoiceCallMessageType ? (byte)(linkControl[7] & 0x3F) : (byte)0,
            []);
        return true;
    }

    private static byte[] CreatePacketHeader(uint sourceId, uint destinationId, bool group, byte messageType, byte frameSequence)
    {
        byte[] packet = new byte[PacketBytes];
        packet[0] = (byte)'N'; packet[1] = (byte)'X'; packet[2] = (byte)'D'; packet[3] = (byte)'D';
        packet[4] = messageType;
        WriteThreeBytes(packet, 5, sourceId);
        WriteThreeBytes(packet, 8, destinationId);
        packet[14] = frameSequence;
        if (!group)
            packet[15] = 0x40;
        packet[23] = (byte)DeclaredPacketBytes;
        packet[24] = 0x01; // dvmhost modem::TAG_DATA
        packet[25] = 0x00;
        return packet;
    }

    private static bool TryExtractFrameAt(ReadOnlySpan<byte> packet, int offset, Span<byte> frame)
    {
        if (offset < 0 || packet.Length < offset + FrameBytes)
            return false;

        Span<byte> candidate = stackalloc byte[FrameBytes];
        packet.Slice(offset, FrameBytes).CopyTo(candidate);
        Scramble(candidate);
        if (candidate[0] != 0xCD || candidate[1] != 0xF5 || (candidate[2] & 0xF0) != 0x90)
            return false;

        byte lich = DecodeLich(candidate);
        if (!HasValidLichParity(lich))
            return false;

        packet.Slice(offset, FrameBytes).CopyTo(frame);
        return true;
    }

    private static void AddSync(Span<byte> frame)
    {
        frame[0] = 0xCD;
        frame[1] = 0xF5;
        frame[2] = (byte)((frame[2] & 0x0F) | 0x90);
    }

    private static byte DecodeLich(ReadOnlySpan<byte> frame)
    {
        byte value = 0;
        for (int index = 0; index < 8; index++)
            SetBit(ref value, index, GetBit(frame, 20 + (index * 2)));
        return value;
    }

    private static void EncodeLich(Span<byte> frame, byte functionChannelType, byte option)
    {
        byte value = (byte)(0x80 | ((functionChannelType & 0x03) << 4) | ((option & 0x03) << 2) | 0x02);
        if ((value & 0xF0) is 0x80 or 0xB0)
            value |= 0x01;
        for (int index = 0; index < 8; index++)
        {
            SetBit(frame, 20 + (index * 2), GetBit(value, index));
            SetBit(frame, 21 + (index * 2), true);
        }
    }

    private static bool HasValidLichParity(byte value)
        => (value & 0x01) == (((value & 0xF0) is 0x80 or 0xB0) ? 1 : 0);

    private static void EncodeSacch(
        Span<byte> frame,
        byte ran,
        byte structure,
        ReadOnlySpan<byte> payload)
    {
        Span<byte> data = stackalloc byte[5];
        data.Clear();
        data[0] = (byte)((structure << 6) | ran);
        for (int bit = 0; bit < 18; bit++)
            SetBit(data, 8 + bit, GetBit(payload, bit));
        ushort crc = CreateCrc(data, 26, 6, 0x27, 0x3F);
        for (int index = 0; index < 6; index++)
            SetBit(data, 26 + index, ((crc >> (5 - index)) & 1) != 0);
        Span<bool> convolution = stackalloc bool[72];
        EncodeConvolution(data, 36, convolution);
        int output = 0;
        for (int index = 0; index < convolution.Length; index++)
        {
            if (SacchPunctures.Contains(index))
                continue;
            SetBit(frame, SacchOffsetBits + SacchInterleave[output], convolution[index]);
            output++;
        }
    }

    private static void EncodeFacch1(Span<byte> frame, int offset, ReadOnlySpan<byte> linkControl)
    {
        Span<byte> data = stackalloc byte[12];
        data.Clear();
        linkControl[..10].CopyTo(data);
        ushort crc = CreateCrc(data, 80, 12, 0x80F, 0xFFF);
        for (int index = 0; index < 12; index++)
            SetBit(data, 80 + index, ((crc >> (11 - index)) & 1) != 0);
        Span<bool> convolution = stackalloc bool[192];
        EncodeConvolution(data, 96, convolution);
        int output = 0;
        for (int index = 0; index < convolution.Length; index++)
        {
            if (FacchPunctures.Contains(index))
                continue;
            SetBit(frame, offset + FacchInterleave[output], convolution[index]);
            output++;
        }
    }

    private static bool TryDecodeFacch1(ReadOnlySpan<byte> frame, int offset, out byte[] data)
    {
        Span<int> symbols = stackalloc int[192];
        symbols.Fill(-1);
        int input = 0;
        for (int index = 0; index < symbols.Length; index++)
        {
            if (FacchPunctures.Contains(index))
                continue;
            symbols[index] = GetBit(frame, offset + FacchInterleave[input++]) ? 1 : 0;
        }

        data = new byte[12];
        return TryDecodeConvolution(symbols, data, decodedBitCount: 96) &&
            CreateCrc(data, 80, 12, 0x80F, 0xFFF) == ReadBits(data, 80, 12);
    }

    private static bool TryDecodeConvolution(
        ReadOnlySpan<int> symbols,
        Span<byte> decoded,
        int decodedBitCount)
    {
        const int StateCount = 16;
        const int UnreachableMetric = 10_000;
        if (symbols.Length < decodedBitCount * 2 || decoded.Length * 8 < decodedBitCount)
            return false;

        Span<int> metrics = stackalloc int[StateCount];
        Span<int> next = stackalloc int[StateCount];
        metrics.Fill(UnreachableMetric);
        metrics[0] = 0;
        int[,] previous = new int[decodedBitCount, StateCount];
        byte[,] decisions = new byte[decodedBitCount, StateCount];
        for (int step = 0; step < decodedBitCount; step++)
        {
            next.Fill(UnreachableMetric);
            for (int state = 0; state < StateCount; state++)
            {
                if (metrics[state] >= UnreachableMetric)
                    continue;
                for (int bit = 0; bit <= 1; bit++)
                {
                    int d1 = state & 1;
                    int d2 = (state >> 1) & 1;
                    int d3 = (state >> 2) & 1;
                    int d4 = (state >> 3) & 1;
                    int g1 = bit ^ d3 ^ d4;
                    int g2 = bit ^ d1 ^ d2 ^ d4;
                    int newState = ((state << 1) | bit) & 0x0F;
                    int cost = metrics[state];
                    int s0 = symbols[step * 2];
                    int s1 = symbols[(step * 2) + 1];
                    if (s0 >= 0 && s0 != g1) cost++;
                    if (s1 >= 0 && s1 != g2) cost++;
                    if (cost < next[newState])
                    {
                        next[newState] = cost;
                        previous[step, newState] = state;
                        decisions[step, newState] = (byte)bit;
                    }
                }
            }
            next.CopyTo(metrics);
        }

        int finalState = 0;
        decoded.Clear();
        for (int step = decodedBitCount - 1; step >= 0; step--)
        {
            SetBit(decoded, step, decisions[step, finalState] != 0);
            finalState = previous[step, finalState];
        }
        return true;
    }

    private static void EncodeConvolution(ReadOnlySpan<byte> input, int bitCount, Span<bool> output)
    {
        int d1 = 0, d2 = 0, d3 = 0, d4 = 0;
        for (int index = 0; index < bitCount; index++)
        {
            int d = GetBit(input, index) ? 1 : 0;
            output[index * 2] = (d ^ d3 ^ d4) != 0;
            output[(index * 2) + 1] = (d ^ d1 ^ d2 ^ d4) != 0;
            d4 = d3; d3 = d2; d2 = d1; d1 = d;
        }
    }

    private static ushort CreateCrc(ReadOnlySpan<byte> data, int bitCount, int width, ushort polynomial, ushort initial)
    {
        ushort crc = initial;
        ushort top = (ushort)(1 << (width - 1));
        ushort mask = (ushort)((1 << width) - 1);
        for (int index = 0; index < bitCount; index++)
        {
            bool input = GetBit(data, index);
            bool high = (crc & top) != 0;
            crc = (ushort)((crc << 1) & mask);
            if (input ^ high)
                crc ^= polynomial;
        }
        return (ushort)(crc & mask);
    }

    private static int ReadBits(ReadOnlySpan<byte> data, int offset, int count)
    {
        int value = 0;
        for (int index = 0; index < count; index++)
            value = (value << 1) | (GetBit(data, offset + index) ? 1 : 0);
        return value;
    }

    private static void Scramble(Span<byte> frame)
    {
        for (int index = 0; index < FrameBytes; index++)
            frame[index] ^= Scrambler[index];
    }

    private static bool GetBit(ReadOnlySpan<byte> data, int bit)
        => (data[bit / 8] & (0x80 >> (bit % 8))) != 0;

    private static bool GetBit(byte value, int bit)
        => (value & (0x80 >> bit)) != 0;

    private static void SetBit(Span<byte> data, int bit, bool value)
    {
        byte mask = (byte)(0x80 >> (bit % 8));
        if (value) data[bit / 8] |= mask;
        else data[bit / 8] &= (byte)~mask;
    }

    private static void SetBit(ref byte data, int bit, bool value)
    {
        byte mask = (byte)(0x80 >> bit);
        if (value) data |= mask;
        else data &= (byte)~mask;
    }

    private static void ValidateIds(uint sourceId, uint destinationId)
    {
        if (sourceId is 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sourceId), "NXDN radio IDs are 16-bit non-zero values.");
        if (destinationId is 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(destinationId), "NXDN destination IDs are 16-bit non-zero values.");
    }

    private static CallMetadata CreateVoiceCallMetadata(
        uint sourceId,
        uint destinationId,
        bool group,
        byte cipherType,
        byte keyId)
        => new(
            VoiceCallMessageType,
            (ushort)sourceId,
            (ushort)destinationId,
            group,
            cipherType,
            keyId,
            []);

    private static CallMetadata CreateVoiceCallIvMetadata(ReadOnlySpan<byte> messageIndicator)
    {
        if (messageIndicator.Length < NxdnPrivacyAlgorithms.MessageIndicatorBytes)
        {
            throw new ArgumentException(
                "NXDN DES/AES privacy requires an 8-byte message indicator.",
                nameof(messageIndicator));
        }

        return new CallMetadata(
            VoiceCallIvMessageType,
            0,
            0,
            true,
            0,
            0,
            messageIndicator[..NxdnPrivacyAlgorithms.MessageIndicatorBytes].ToArray());
    }

    private static void WriteCallMetadata(Span<byte> linkControl, CallMetadata metadata)
    {
        linkControl.Clear();
        linkControl[0] = metadata.MessageType;
        if (metadata.MessageType == VoiceCallIvMessageType)
        {
            if (metadata.MessageIndicator.Length < NxdnPrivacyAlgorithms.MessageIndicatorBytes)
            {
                throw new ArgumentException(
                    "NXDN VCALL_IV metadata requires an 8-byte message indicator.",
                    nameof(metadata));
            }
            metadata.MessageIndicator.AsSpan(0, NxdnPrivacyAlgorithms.MessageIndicatorBytes)
                .CopyTo(linkControl[1..]);
            return;
        }
        if (metadata.MessageType is not (VoiceCallMessageType or TransmitReleaseMessageType))
            throw new ArgumentOutOfRangeException(nameof(metadata));

        linkControl[2] = (byte)((metadata.Group ? 1 : 4) << 5);
        linkControl[3] = (byte)(metadata.SourceId >> 8);
        linkControl[4] = (byte)metadata.SourceId;
        linkControl[5] = (byte)(metadata.DestinationId >> 8);
        linkControl[6] = (byte)metadata.DestinationId;
        if (metadata.MessageType == VoiceCallMessageType)
            linkControl[7] = (byte)((metadata.CipherType << 6) | metadata.KeyId);
    }

    private static void WriteThreeBytes(Span<byte> target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 16);
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)value;
    }
}
