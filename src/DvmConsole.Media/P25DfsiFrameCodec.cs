// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using fnecore.P25;
using System.Runtime.CompilerServices;

namespace DvmConsole.Media;

// Extracts the nine 88-bit IMBE information frames from one complete P25
// DFSI LDU. P25 Phase 1 carries 18-byte Annex-H FEC frames over the air, but
// the FNE DFSI records contain only the 11-byte post-FEC information layer.
// Do not manufacture 18-byte frames here: re-encoding already-recovered bits
// would falsely report a clean channel. Missing or malformed DFSI frame slots
// are instead submitted to the stateful vocoder as erasures by
// P25RxAudioSession, allowing its native repeat/fade/mute concealment to feed
// both live playback and TAR. DVMHost does not populate reliable per-record
// FEC status in this layout, so reserved/trailing bytes must not be treated as
// erasure flags without a new explicit peer contract.
public static class P25DfsiFrameCodec
{
    public const int HeaderBytes = 24;
    public const int RecordLengthOffset = 23;
    public const int RecordBytes = 154;
    public const int CodewordBytes = 11;
    public const int CodewordsPerLdu = 9;
    public const int ImbeBytes = CodewordBytes * CodewordsPerLdu;
    public const int NetworkPayloadBytes = 200;
    public const int ClearLduPayloadLength = HeaderBytes + RecordBytes;
    public const int TduPayloadLength = HeaderBytes;
    public const byte Ldu1Duid = 0x05;
    public const byte Ldu2Duid = 0x0A;
    public const byte TduDuid = 0x03;
    public const ushort RtpCallEndSequence = ushort.MaxValue;
    private const byte VoiceServicePriority4 = 0x04;
    private const byte VoiceServiceProtected = 0x40;

    public readonly record struct P25EncryptionMetadata(
        byte AlgorithmId,
        ushort KeyId,
        ReadOnlyMemory<byte> MessageIndicator);

    public readonly record struct P25ParsedLdu(
        bool IsLdu1,
        bool HasEncryptionMetadata,
        byte AlgorithmId,
        ushort KeyId);

    private static readonly byte[] Ldu1RecordTypes = [0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A];
    private static readonly byte[] Ldu2RecordTypes = [0x6B, 0x6C, 0x6D, 0x6E, 0x6F, 0x70, 0x71, 0x72, 0x73];
    private static readonly int[] RecordLengths = [22, 14, 17, 17, 17, 17, 17, 17, 16];
    private static readonly int[] RecordOffsets = [0, 22, 36, 53, 70, 87, 104, 121, 138];
    private static readonly int[] CodewordOffsets = [10, 1, 5, 5, 5, 5, 5, 5, 4];
    private static readonly ConditionalWeakTable<IRadioMediaFrame, Lazy<ParsedVoiceLduCache>>
        ParsedVoiceLdus = new();

    public static bool TryExtractImbe(IRadioMediaFrame traffic, Span<byte> imbe)
    {
        Span<bool> available = stackalloc bool[CodewordsPerLdu];
        return TryExtractImbeFrames(traffic, imbe, available) &&
            !available.Contains(false);
    }

    // Extracts every independently valid 20 ms voice record from an LDU.
    // A damaged DFSI record must not discard the other eight codewords: the
    // receive session submits only the unavailable slot as a decoder erasure.
    public static bool TryExtractImbeFrames(
        IRadioMediaFrame traffic,
        Span<byte> imbe,
        Span<bool> available)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.Protocol != RadioMediaProtocol.P25 ||
            !IsVoiceLdu(traffic) ||
            imbe.Length < ImbeBytes ||
            available.Length < CodewordsPerLdu)
        {
            return false;
        }

        ParsedVoiceLduCache cached = GetParsedVoiceLdu(traffic);
        if (!cached.IsValid)
            return false;
        cached.CopyImbeTo(imbe, available);
        return true;
    }

    // Parses the record layout once and writes every receive artifact into
    // caller-owned buffers. The public compatibility helpers below delegate
    // to the same core and allocate only when their owned result requires it.
    public static bool TryParseVoiceLdu(
        IRadioMediaFrame traffic,
        Span<byte> imbe,
        Span<bool> available,
        Span<byte> messageIndicator,
        out P25ParsedLdu parsed)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        parsed = default;
        if (traffic.Protocol != RadioMediaProtocol.P25 ||
            !IsVoiceLdu(traffic) ||
            imbe.Length < ImbeBytes ||
            available.Length < CodewordsPerLdu ||
            messageIndicator.Length < P25Defines.P25_MI_LENGTH)
        {
            return false;
        }

        ParsedVoiceLduCache cached = GetParsedVoiceLdu(traffic);
        if (!cached.IsValid)
            return false;
        cached.CopyTo(imbe, available, messageIndicator, out parsed);
        return true;
    }

    public static byte[] ExtractImbe(IRadioMediaFrame traffic)
    {
        byte[] imbe = new byte[ImbeBytes];
        if (!TryExtractImbe(traffic, imbe))
            throw new ArgumentException("The P25 packet does not contain a complete voice LDU.", nameof(traffic));
        return imbe;
    }

    // Prefer the identifiers carried inside the DFSI records over placeholder
    // values in the outer FNE event. Some peers use WUID_FNE in the event
    // header until link control arrives, even though LDU1 contains the real
    // subscriber and talkgroup identifiers.
    public static bool TryExtractCallIdentifiers(
        IRadioMediaFrame traffic,
        out uint sourceId,
        out uint destinationId)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        sourceId = 0;
        destinationId = 0;
        if (traffic.Protocol != RadioMediaProtocol.P25 || !IsVoiceLdu(traffic))
            return false;

        ReadOnlySpan<byte> payload = traffic.Payload;
        if (payload.Length < HeaderBytes ||
            payload[0] != (byte)'P' ||
            payload[1] != (byte)'2' ||
            payload[2] != (byte)'5' ||
            payload[3] != (byte)'D')
        {
            return false;
        }

        bool ldu1 = traffic.Subtype.Equals("LDU1", StringComparison.OrdinalIgnoreCase);
        if (ldu1)
        {
            ParsedVoiceLduCache cached = GetParsedVoiceLdu(traffic);
            if (!cached.IsValid ||
                !cached.IsCodewordAvailable(3) ||
                !cached.IsCodewordAvailable(4))
            {
                return false;
            }

            destinationId = ReadThreeBytes(payload, 78);
            sourceId = ReadThreeBytes(payload, 95);
        }

        if (sourceId == 0)
            sourceId = ReadThreeBytes(payload, 5);
        if (destinationId == 0)
            destinationId = ReadThreeBytes(payload, 8);
        return sourceId != 0 && destinationId != 0;
    }

    // Extracts the legacy P25 encryption metadata carried by a voice LDU.
    // LDU1 contains the HDU fields after its DFSI records. LDU2 contains the
    // next message indicator and key identity in its encryption-sync records.
    // A clear LDU2 has zeroed sync fields and returns false.
    public static bool TryExtractEncryptionMetadata(
        IRadioMediaFrame traffic,
        out P25EncryptionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        metadata = default;
        if (traffic.Protocol != RadioMediaProtocol.P25 || !IsVoiceLdu(traffic))
            return false;

        ParsedVoiceLduCache cached = GetParsedVoiceLdu(traffic);
        if (!cached.IsValid || !cached.Parsed.HasEncryptionMetadata)
        {
            return false;
        }

        metadata = new P25EncryptionMetadata(
            cached.Parsed.AlgorithmId,
            cached.Parsed.KeyId,
            cached.CopyMessageIndicator());
        return true;
    }

    public static bool TryExtractEncryptionIdentity(
        IRadioMediaFrame traffic,
        out byte algorithmId,
        out ushort keyId)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        algorithmId = 0;
        keyId = 0;
        if (traffic.Protocol != RadioMediaProtocol.P25 || !IsVoiceLdu(traffic))
            return false;

        ParsedVoiceLduCache cached = GetParsedVoiceLdu(traffic);
        if (!cached.IsValid || !cached.Parsed.HasEncryptionMetadata)
            return false;
        algorithmId = cached.Parsed.AlgorithmId;
        keyId = cached.Parsed.KeyId;
        return true;
    }

    private static ParsedVoiceLduCache GetParsedVoiceLdu(IRadioMediaFrame traffic)
        => ParsedVoiceLdus.GetValue(
            traffic,
            static frame => new Lazy<ParsedVoiceLduCache>(
                () => new ParsedVoiceLduCache(frame),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    // An ingress adapter may replace only the outer call identity after LDU1
    // link control resolves placeholder IDs. Associate that immutable
    // projection with the already-computed analysis so downstream encryption,
    // recording, and decoder consumers do not parse the same LDU again.
    public static void ShareParsedVoiceLdu(
        IRadioMediaFrame source,
        IRadioMediaFrame projection)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(projection);
        if (ReferenceEquals(source, projection) ||
            source.Protocol != RadioMediaProtocol.P25 ||
            projection.Protocol != RadioMediaProtocol.P25 ||
            !IsVoiceLdu(source) ||
            !IsVoiceLdu(projection))
        {
            return;
        }

        Lazy<ParsedVoiceLduCache> analysis = ParsedVoiceLdus.GetValue(
            source,
            static frame => new Lazy<ParsedVoiceLduCache>(
                () => new ParsedVoiceLduCache(frame),
                LazyThreadSafetyMode.ExecutionAndPublication));
        _ = ParsedVoiceLdus.GetValue(projection, _ => analysis);
    }

    private static bool TryExtractEncryptionMetadataCore(
        ReadOnlySpan<byte> payload,
        bool ldu1,
        ReadOnlySpan<bool> available,
        Span<byte> messageIndicator,
        out byte algorithmId,
        out ushort keyId)
    {
        messageIndicator[..P25Defines.P25_MI_LENGTH].Clear();
        algorithmId = 0;
        keyId = 0;

        if (ldu1)
        {
            if (payload.Length < 193 || payload[180] != 0x01)
                return false;

            algorithmId = payload[181];
            keyId = (ushort)((payload[182] << 8) | payload[183]);
            // Older and third-party FNE peers sometimes emit an HDU-valid
            // clear header with zeroed crypto fields. The legacy console
            // normalizes algorithm 0/key 0 to the P25 UNENCRYPT value.
            if (algorithmId == 0 && keyId == 0)
                algorithmId = P25Defines.P25_ALGO_UNENCRYPT;
            payload.Slice(184, P25Defines.P25_MI_LENGTH).CopyTo(messageIndicator);
            return true;
        }

        // LDU2 ESS fields span voice records 12 through 15. Do not prepare a
        // future key stream from metadata whose containing record was damaged.
        if (!available[2] || !available[3] || !available[4] || !available[5] ||
            payload.Length <= 114 || payload[112] == 0)
            return false;

        payload.Slice(61, 3).CopyTo(messageIndicator[..3]);
        payload.Slice(78, 3).CopyTo(messageIndicator.Slice(3, 3));
        payload.Slice(95, 3).CopyTo(messageIndicator.Slice(6, 3));
        algorithmId = payload[112];
        keyId = (ushort)((payload[113] << 8) | payload[114]);
        if (algorithmId == 0 && keyId == 0)
            algorithmId = P25Defines.P25_ALGO_UNENCRYPT;
        return true;
    }

    // Builds a clear P25 LDU1 payload from nine 88-bit IMBE codewords.
    public static byte[] CreateLdu1Payload(uint sourceId, uint destinationId, ReadOnlySpan<byte> imbe)
        => CreateLdu1Payload(sourceId, destinationId, imbe, encrypted: false);

    private static byte[] CreateLdu1Payload(
        uint sourceId,
        uint destinationId,
        ReadOnlySpan<byte> imbe,
        bool encrypted)
    {
        ValidateIdentifiers(sourceId, destinationId);
        ValidateImbe(imbe);
        byte[] payload = CreateHeader(Ldu1Duid, sourceId, destinationId);

        Span<byte> record = WriteRecord(payload, 24, 22, 0x62, imbe[0..11], 10);
        record[6] = 0;
        WriteRecord(payload, 46, 14, 0x63, imbe[11..22], 1);
        record = WriteRecord(payload, 60, 17, 0x64, imbe[22..33], 5);
        record[1] = 0;
        record[2] = 0;
        record[3] = BuildVoiceServiceOptions(encrypted);
        record = WriteRecord(payload, 77, 17, 0x65, imbe[33..44], 5);
        WriteThreeBytes(record, 1, destinationId);
        record = WriteRecord(payload, 94, 17, 0x66, imbe[44..55], 5);
        WriteThreeBytes(record, 1, sourceId);
        WriteRecord(payload, 111, 17, 0x67, imbe[55..66], 5);
        WriteRecord(payload, 128, 17, 0x68, imbe[66..77], 5);
        WriteRecord(payload, 145, 17, 0x69, imbe[77..88], 5);
        WriteRecord(payload, 162, 16, 0x6A, imbe[88..99], 4);
        WriteClearEncryptionHeader(payload);
        return payload;
    }

    // Builds an encrypted P25 LDU1 payload. The nine IMBE codewords must
    // already be encrypted with the supplied key stream. The initial HDU
    // metadata follows the layout emitted by the legacy FNE adapter.
    public static byte[] CreateEncryptedLdu1Payload(
        uint sourceId,
        uint destinationId,
        ReadOnlySpan<byte> encryptedImbe,
        P25EncryptionMetadata metadata)
    {
        ValidateEncryptionMetadata(metadata);
        byte[] payload = CreateLdu1Payload(
            sourceId,
            destinationId,
            encryptedImbe,
            encrypted: true);
        WriteEncryptionHeader(payload, metadata);
        return payload;
    }

    private static byte BuildVoiceServiceOptions(bool encrypted)
        => (byte)(VoiceServicePriority4 | (encrypted ? VoiceServiceProtected : 0));

    // Builds a clear P25 LDU2 payload from nine 88-bit IMBE codewords.
    // The encryption-sync fields are zeroed for clear traffic.
    public static byte[] CreateLdu2Payload(uint sourceId, uint destinationId, ReadOnlySpan<byte> imbe)
    {
        ValidateIdentifiers(sourceId, destinationId);
        ValidateImbe(imbe);
        byte[] payload = CreateHeader(Ldu2Duid, sourceId, destinationId);

        Span<byte> record = WriteRecord(payload, 24, 22, 0x6B, imbe[0..11], 10);
        record[6] = 0;
        WriteRecord(payload, 46, 14, 0x6C, imbe[11..22], 1);
        WriteRecord(payload, 60, 17, 0x6D, imbe[22..33], 5);
        WriteRecord(payload, 77, 17, 0x6E, imbe[33..44], 5);
        WriteRecord(payload, 94, 17, 0x6F, imbe[44..55], 5);
        WriteRecord(payload, 111, 17, 0x70, imbe[55..66], 5);
        WriteRecord(payload, 128, 17, 0x71, imbe[66..77], 5);
        WriteRecord(payload, 145, 17, 0x72, imbe[77..88], 5);
        WriteRecord(payload, 162, 16, 0x73, imbe[88..99], 4);
        // Clear P25 still carries an explicit UNENCRYPT algorithm in the
        // encryption-sync fields. Zero is not a valid clear algorithm ID.
        payload[112] = P25Defines.P25_ALGO_UNENCRYPT;
        WriteClearEncryptionHeader(payload);
        return payload;
    }

    // Builds an encrypted P25 LDU2 payload. The metadata carries the next
    // message indicator, matching the legacy transmitter's MI advance after
    // the current LDU2 has been encrypted.
    public static byte[] CreateEncryptedLdu2Payload(
        uint sourceId,
        uint destinationId,
        ReadOnlySpan<byte> encryptedImbe,
        P25EncryptionMetadata metadata)
    {
        ValidateEncryptionMetadata(metadata);
        byte[] payload = CreateLdu2Payload(sourceId, destinationId, encryptedImbe);

        // The LDU2 encryption-sync records carry the next MI and key identity.
        metadata.MessageIndicator.Span[..3].CopyTo(payload.AsSpan(61, 3));
        metadata.MessageIndicator.Span.Slice(3, 3).CopyTo(payload.AsSpan(78, 3));
        metadata.MessageIndicator.Span.Slice(6, 3).CopyTo(payload.AsSpan(95, 3));
        payload[112] = metadata.AlgorithmId;
        payload[113] = (byte)(metadata.KeyId >> 8);
        payload[114] = (byte)metadata.KeyId;

        // The legacy header also carries the encryption fields outside the
        // declared DFSI length; keep those fields consistent for FNE peers
        // that inspect the network header directly.
        WriteEncryptionHeader(payload, metadata);
        return payload;
    }

    // Builds the legacy P25 TDU control payload used to request or terminate
    // a clear group call. The FNE RTP call-end sequence is supplied by the
    // call session, not embedded in this protocol payload.
    public static byte[] CreateTduPayload(uint sourceId, uint destinationId, bool grantDemand)
    {
        ValidateIdentifiers(sourceId, destinationId);
        byte[] payload = CreateHeader(TduDuid, sourceId, destinationId);
        payload[RecordLengthOffset] = (byte)TduPayloadLength;
        if (grantDemand)
            payload[14] |= 0x80;
        return payload;
    }

    private static bool TryExtractImbeFrames(
        ReadOnlySpan<byte> payload,
        bool ldu1,
        Span<byte> imbe,
        Span<bool> available)
    {
        imbe[..ImbeBytes].Clear();
        available[..CodewordsPerLdu].Clear();
        if (payload.Length <= RecordLengthOffset ||
            payload.Length < HeaderBytes ||
            payload[RecordLengthOffset] < HeaderBytes ||
            payload[RecordLengthOffset] > payload.Length)
            return false;

        int recordBytes = payload[RecordLengthOffset] - HeaderBytes;
        if (recordBytes < RecordBytes)
            return false;

        ReadOnlySpan<byte> records = payload.Slice(HeaderBytes, recordBytes);
        ReadOnlySpan<byte> expectedTypes = ldu1 ? Ldu1RecordTypes : Ldu2RecordTypes;
        for (int index = 0; index < RecordLengths.Length; index++)
        {
            int recordLength = RecordLengths[index];
            int recordOffset = RecordOffsets[index];
            int codewordOffset = CodewordOffsets[index];
            if (recordOffset + recordLength > records.Length ||
                records[recordOffset] != expectedTypes[index] ||
                codewordOffset + CodewordBytes > recordLength)
            {
                continue;
            }

            records.Slice(recordOffset + codewordOffset, CodewordBytes)
                .CopyTo(imbe[(index * CodewordBytes)..]);
            available[index] = true;
        }

        return true;
    }

    [InlineArray(ImbeBytes)]
    private struct ImbeBuffer
    {
        private byte element0;
    }

    [InlineArray(CodewordsPerLdu)]
    private struct AvailabilityBuffer
    {
        private bool element0;
    }

    [InlineArray(P25Defines.P25_MI_LENGTH)]
    private struct MessageIndicatorBuffer
    {
        private byte element0;
    }

    private sealed class ParsedVoiceLduCache
    {
        private ImbeBuffer imbe;
        private AvailabilityBuffer available;
        private MessageIndicatorBuffer messageIndicator;

        public ParsedVoiceLduCache(IRadioMediaFrame traffic)
        {
            bool ldu1 = string.Equals(
                traffic.Subtype,
                "LDU1",
                StringComparison.OrdinalIgnoreCase);
            Span<byte> imbeSpan = imbe;
            Span<bool> availableSpan = available;
            IsValid = TryExtractImbeFrames(
                traffic.Payload,
                ldu1,
                imbeSpan,
                availableSpan);
            if (!IsValid)
                return;

            Span<byte> messageIndicatorSpan = messageIndicator;
            bool hasEncryptionMetadata = TryExtractEncryptionMetadataCore(
                traffic.Payload,
                ldu1,
                availableSpan,
                messageIndicatorSpan,
                out byte algorithmId,
                out ushort keyId);
            Parsed = new P25ParsedLdu(
                ldu1,
                hasEncryptionMetadata,
                algorithmId,
                keyId);
        }

        public bool IsValid { get; }
        public P25ParsedLdu Parsed { get; }

        public bool IsCodewordAvailable(int index) => available[index];

        public void CopyImbeTo(Span<byte> destination, Span<bool> availability)
        {
            ReadOnlySpan<byte> sourceImbe = imbe;
            ReadOnlySpan<bool> sourceAvailability = available;
            sourceImbe.CopyTo(destination);
            sourceAvailability.CopyTo(availability);
        }

        public void CopyTo(
            Span<byte> destination,
            Span<bool> availability,
            Span<byte> destinationMessageIndicator,
            out P25ParsedLdu parsed)
        {
            CopyImbeTo(destination, availability);
            ReadOnlySpan<byte> sourceMessageIndicator = messageIndicator;
            sourceMessageIndicator.CopyTo(destinationMessageIndicator);
            parsed = Parsed;
        }

        public byte[] CopyMessageIndicator()
        {
            ReadOnlySpan<byte> source = messageIndicator;
            return source.ToArray();
        }
    }

    private static bool IsVoiceLdu(IRadioMediaFrame traffic)
    {
        return string.Equals(traffic.FrameType, "VOICE", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(traffic.Subtype, "LDU1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(traffic.Subtype, "LDU2", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] CreateHeader(byte duid, uint sourceId, uint destinationId)
    {
        byte[] payload = new byte[NetworkPayloadBytes];
        payload[0] = (byte)'P';
        payload[1] = (byte)'2';
        payload[2] = (byte)'5';
        payload[3] = (byte)'D';
        payload[4] = 0;
        WriteThreeBytes(payload, 5, sourceId);
        WriteThreeBytes(payload, 8, destinationId);
        payload[22] = duid;
        payload[23] = (byte)ClearLduPayloadLength;
        return payload;
    }

    private static Span<byte> WriteRecord(
        byte[] payload,
        int offset,
        int length,
        byte frameType,
        ReadOnlySpan<byte> imbe,
        int imbeOffset)
    {
        Span<byte> record = payload.AsSpan(offset, length);
        record.Clear();
        record[0] = frameType;
        imbe.CopyTo(record.Slice(imbeOffset, CodewordBytes));
        return record;
    }

    private static void ValidateImbe(ReadOnlySpan<byte> imbe)
    {
        if (imbe.Length < ImbeBytes)
            throw new ArgumentException($"IMBE data must contain {ImbeBytes} bytes.", nameof(imbe));
    }

    private static void ValidateEncryptionMetadata(P25EncryptionMetadata metadata)
    {
        if (metadata.AlgorithmId is not (P25Defines.P25_ALGO_DES or P25Defines.P25_ALGO_AES or P25Defines.P25_ALGO_ARC4))
            throw new ArgumentException($"Unsupported P25 encryption algorithm 0x{metadata.AlgorithmId:X2}.", nameof(metadata));
        if (metadata.KeyId == 0)
            throw new ArgumentOutOfRangeException(nameof(metadata), "P25 encryption key ID must be non-zero.");
        if (metadata.MessageIndicator.Length < P25Defines.P25_MI_LENGTH)
            throw new ArgumentException("P25 encryption metadata requires a 9-byte message indicator.", nameof(metadata));
    }

    private static void WriteEncryptionHeader(byte[] payload, P25EncryptionMetadata metadata)
    {
        payload[14] |= 0x08;
        payload[180] = P25Defines.P25_FT_HDU_VALID;
        payload[181] = metadata.AlgorithmId;
        payload[182] = (byte)(metadata.KeyId >> 8);
        payload[183] = (byte)metadata.KeyId;
        metadata.MessageIndicator.Span[..P25Defines.P25_MI_LENGTH]
            .CopyTo(payload.AsSpan(184, P25Defines.P25_MI_LENGTH));
    }

    private static void WriteClearEncryptionHeader(byte[] payload)
    {
        // Match fnecore's clear CryptoParams contract: HDU metadata is valid,
        // but the algorithm explicitly identifies unencrypted voice.
        payload[14] |= 0x08;
        payload[180] = P25Defines.P25_FT_HDU_VALID;
        payload[181] = P25Defines.P25_ALGO_UNENCRYPT;
    }

    private static void ValidateIdentifiers(uint sourceId, uint destinationId)
    {
        if (sourceId == 0 || sourceId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId == 0 || destinationId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
    }

    private static void WriteThreeBytes(Span<byte> target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 16);
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)value;
    }

    private static uint ReadThreeBytes(ReadOnlySpan<byte> source, int offset)
    {
        if (offset < 0 || source.Length < offset + 3)
            return 0;
        return (uint)((source[offset] << 16) | (source[offset + 1] << 8) | source[offset + 2]);
    }
}
