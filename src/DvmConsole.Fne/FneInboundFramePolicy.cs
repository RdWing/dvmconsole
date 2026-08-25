#nullable enable
// SPDX-License-Identifier: AGPL-3.0-only

namespace fnecore;

internal static class FneInboundFramePolicy
{
    private const int HeaderLength = 32;

    public static bool AcceptsInbound(FneUdpChannelKind channelKind)
        => channelKind == FneUdpChannelKind.Traffic;

    public static bool ShouldDeliverTraffic(byte[] message)
    {
        if (message.Length < HeaderLength ||
            !LooksLikeCompleteHeader(message))
        {
            return false;
        }

        uint declaredLength = ReadUInt32(message.AsSpan(28, 4));
        int availableLength = message.Length - HeaderLength;
        if (declaredLength == 0 || declaredLength > availableLength)
            return false;

        ReadOnlySpan<byte> payload = message.AsSpan(HeaderLength, checked((int)declaredLength));
        byte function = message[18];
        byte subFunction = message[19];
        return HasSafePayload(function, subFunction, payload);
    }

    private static bool LooksLikeCompleteHeader(ReadOnlySpan<byte> message)
    {
        bool versionTwo = ((message[0] >> 6) & 0x03) == 0x02;
        bool hasExtension = (message[0] & 0x10) != 0;
        bool hasNoCsrcEntries = (message[0] & 0x0F) == 0;
        byte payloadType = (byte)(message[1] & 0x7F);
        bool fneExtension = message[12] == 0x00 &&
            message[13] == Constants.DVMFrameStart &&
            message[14] == 0x00 &&
            message[15] == Constants.RtpFNEHeaderExtLength;
        return versionTwo && hasExtension && hasNoCsrcEntries && fneExtension &&
            payloadType is Constants.DVMRtpPayloadType or Constants.DVMRtpPayloadType + 1;
    }

    private static bool HasSafePayload(
        byte function,
        byte subFunction,
        ReadOnlySpan<byte> payload)
        => function switch
        {
            Constants.NET_FUNC_PROTOCOL => subFunction switch
            {
                Constants.NET_PROTOCOL_SUBFUNC_DMR => payload.Length >= 16,
                Constants.NET_PROTOCOL_SUBFUNC_P25 => payload.Length >= 23,
                Constants.NET_PROTOCOL_SUBFUNC_NXDN => payload.Length >= 16,
                Constants.NET_PROTOCOL_SUBFUNC_ANALOG => payload.Length >= 16,
                _ => true
            },
            Constants.NET_FUNC_MASTER => subFunction switch
            {
                Constants.NET_MASTER_SUBFUNC_ACTIVE_TGS => false,
                Constants.NET_MASTER_SUBFUNC_DEACTIVE_TGS => false,
                Constants.NET_MASTER_SUBFUNC_HA_PARAMS => HasSafeHaPayload(payload),
                _ => true
            },
            Constants.NET_FUNC_INCALL_CTRL => subFunction == Constants.NET_PROTOCOL_SUBFUNC_DMR
                ? payload.Length >= 15
                : payload.Length >= 14,
            Constants.NET_FUNC_KEY_RSP => HasSafeKeyResponse(payload),
            Constants.NET_FUNC_ACK => payload.Length >= 10,
            Constants.NET_FUNC_NAK => payload.Length <= 10 || payload.Length >= 12,
            _ => true
        };

    private static bool HasSafeHaPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
            return false;

        uint announcedBytes = ReadUInt32(payload.Slice(6, 4));
        uint entryBytes = Constants.HAParamsEntryLen;
        uint entries = announcedBytes / entryBytes;
        ulong requiredLength = 10UL + (ulong)entries * entryBytes;
        return requiredLength <= (ulong)payload.Length;
    }

    private static bool HasSafeKeyResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12)
            return false;

        ReadOnlySpan<byte> kmm = payload[11..];
        if (kmm[0] != (byte)P25.KMM.KmmMessageType.MODIFY_KEY_CMD)
            return true;
        if (kmm.Length < 18)
            return false;

        int offset = 14;
        if (kmm[10] == P25.P25Defines.KMM_DECRYPT_INSTRUCTION_MI)
            offset += P25.P25Defines.P25_MI_LENGTH;
        if (offset > kmm.Length - 4)
            return false;

        int keyLength = kmm[offset + 2];
        int keyCount = kmm[offset + 3];
        offset += 4;
        for (int index = 0; index < keyCount; index++)
        {
            if (offset > kmm.Length - 5)
                return false;

            int keyNameLength = kmm[offset] & 0x1F;
            int entryLength = 5 + keyNameLength + keyLength;
            if (entryLength > kmm.Length - offset)
                return false;
            offset += entryLength;
        }

        return true;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> value)
        => ((uint)value[0] << 24) |
            ((uint)value[1] << 16) |
            ((uint)value[2] << 8) |
            value[3];
}
