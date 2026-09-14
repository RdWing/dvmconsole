// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using fnecore;
using fnecore.EDAC;
using fnecore.P25;

namespace DvmConsole.FneClient;

public sealed record P25SubscriberResponse(P25SubscriberCommand Command, uint SourceId, uint DestinationId);

/// <summary>Decodes standard subscriber acknowledgements from an FNE P25 TSDU payload.</summary>
public static class P25SubscriberResponseCodec
{
    public static bool TryDecode(ReadOnlySpan<byte> payload,
        [NotNullWhen(true)] out P25SubscriberResponse? response)
    {
        response = null;
        int frameLength = checked((int)P25Defines.P25_TSDU_FRAME_LENGTH_BYTES);
        if (payload.Length < 24 + frameLength || !payload[..4].SequenceEqual("P25D"u8) ||
            payload[22] != (byte)P25DUID.TSDU || payload[23] < 24 + frameLength || payload[23] > payload.Length)
            return false;

        byte[] frame = payload.Slice(24, frameLength).ToArray();
        byte[] encoded = new byte[P25Defines.P25_TSBK_FEC_LENGTH_BYTES];
        byte[] block = new byte[P25Defines.P25_TSBK_LENGTH_BYTES];
        P25Interleaver.Decode(frame, ref encoded, 114, 318);
        if (!new Trellis().Decode12(encoded, ref block) || !CRC.CheckCCITT162(block, P25Defines.P25_TSBK_LENGTH_BYTES))
            return false;
        return TryDecodeBlock(block, out response);
    }

    private static bool TryDecodeBlock(ReadOnlySpan<byte> block,
        [NotNullWhen(true)] out P25SubscriberResponse? response)
    {
        response = null;
        if (block.Length != P25Defines.P25_TSBK_LENGTH_BYTES || (block[0] & 0x40) != 0 || block[1] != 0)
            return false;
        byte opcode = (byte)(block[0] & 0x3F);
        P25SubscriberCommand command;
        uint source;
        uint destination;
        if (opcode == P25Defines.TSBK_IOSP_ACK_RSP)
        {
            // AIV supplies the target address. Extended-address acknowledgements
            // carry network/system information in that field, not a console RID.
            if ((block[2] & 0xC0) != 0x80 || (block[2] & 0x3F) != P25Defines.TSBK_IOSP_CALL_ALRT)
                return false;
            command = P25SubscriberCommand.CallAlert;
            destination = ReadId(block[4..7]);
            source = ReadId(block[7..10]);
        }
        else if (opcode == P25Defines.TSBK_IOSP_EXT_FNCT)
        {
            command = (ExtendedFunction)BinaryPrimitives.ReadUInt16BigEndian(block[2..4]) switch
            {
                ExtendedFunction.CHECK_ACK => P25SubscriberCommand.RadioCheck,
                ExtendedFunction.INHIBIT_ACK => P25SubscriberCommand.Inhibit,
                ExtendedFunction.UNINHIBIT_ACK => P25SubscriberCommand.Uninhibit,
                _ => (P25SubscriberCommand)(-1)
            };
            if (!Enum.IsDefined(command)) return false;
            source = ReadId(block[4..7]);
            destination = ReadId(block[7..10]);
        }
        else return false;
        if (!P25SubscriberCommandCodec.IsValidSubscriberId(source) || !P25SubscriberCommandCodec.IsValidSubscriberId(destination))
            return false;
        response = new(command, source, destination);
        return true;
    }

    private static uint ReadId(ReadOnlySpan<byte> bytes)
        => ((uint)bytes[0] << 16) | ((uint)bytes[1] << 8) | bytes[2];
}
