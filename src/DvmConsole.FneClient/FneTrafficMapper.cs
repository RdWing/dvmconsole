// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using fnecore;
using fnecore.DMR;
using fnecore.P25;
using fnecore.NXDN;
using fnecore.Analog;
using System.Collections.Concurrent;

namespace DvmConsole.FneClient;

internal static class FneTrafficMapper
{
    public static Tuple<byte, byte> ToOpcode(FneTrafficProtocol protocol)
        => protocol switch
        {
            FneTrafficProtocol.Dmr => FneBase.CreateOpcode(
                Constants.NET_FUNC_PROTOCOL,
                Constants.NET_PROTOCOL_SUBFUNC_DMR),
            FneTrafficProtocol.P25 => FneBase.CreateOpcode(
                Constants.NET_FUNC_PROTOCOL,
                Constants.NET_PROTOCOL_SUBFUNC_P25),
            FneTrafficProtocol.Nxdn => FneBase.CreateOpcode(
                Constants.NET_FUNC_PROTOCOL,
                Constants.NET_PROTOCOL_SUBFUNC_NXDN),
            FneTrafficProtocol.Analog => FneBase.CreateOpcode(
                Constants.NET_FUNC_PROTOCOL,
                Constants.NET_PROTOCOL_SUBFUNC_ANALOG),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };

    public static FneTrafficFrame FromDmr(
        DMRDataReceivedEvent args,
        long boundaryTimestamp,
        long transportTimestamp)
        => new(
            FneTrafficProtocol.Dmr,
            args.PeerId,
            args.SrcId,
            args.DstId,
            args.Slot,
            EnumTextCache<CallType>.Get(args.CallType),
            EnumTextCache<FrameType>.Get(args.FrameType),
            GetDmrSubtype(args),
            args.PacketSequence,
            args.StreamId,
            args.Data,
            boundaryTimestamp,
            transportTimestamp);

    private static string GetDmrSubtype(DMRDataReceivedEvent args)
    {
        if (args.Data.Length <= 15)
            return EnumTextCache<DMRDataType>.Get(args.DataType);

        byte control = args.Data[15];
        return (control & 0x20) != 0
            ? EnumTextCache<DMRDataType>.Get((DMRDataType)(control & 0x0F))
            : EnumTextCache<DMRDataType>.Get(args.DataType);
    }

    public static FneTrafficFrame FromP25(
        P25DataReceivedEvent args,
        long boundaryTimestamp,
        long transportTimestamp)
        => new(
            FneTrafficProtocol.P25,
            args.PeerId,
            args.SrcId,
            args.DstId,
            null,
            EnumTextCache<CallType>.Get(args.CallType),
            EnumTextCache<FrameType>.Get(args.FrameType),
            EnumTextCache<P25DUID>.Get(args.DUID),
            args.PacketSequence,
            args.StreamId,
            args.Data,
            boundaryTimestamp,
            transportTimestamp);

    public static FneTrafficFrame FromNxdn(
        NXDNDataReceivedEvent args,
        long boundaryTimestamp,
        long transportTimestamp)
        => new(
            FneTrafficProtocol.Nxdn,
            args.PeerId,
            args.SrcId,
            args.DstId,
            null,
            EnumTextCache<CallType>.Get(args.CallType),
            EnumTextCache<FrameType>.Get(args.FrameType),
            EnumTextCache<NXDNMessageType>.Get(args.MessageType),
            args.PacketSequence,
            args.StreamId,
            args.Data,
            boundaryTimestamp,
            transportTimestamp);

    public static FneTrafficFrame FromAnalog(
        AnalogDataReceivedEvent args,
        long boundaryTimestamp,
        long transportTimestamp)
        => new(
            FneTrafficProtocol.Analog,
            args.PeerId,
            args.SrcId,
            args.DstId,
            null,
            EnumTextCache<CallType>.Get(args.CallType),
            EnumTextCache<FrameType>.Get(args.FrameType),
            EnumTextCache<AudioFrameType>.Get(args.AudioFrameType),
            args.PacketSequence,
            args.StreamId,
            args.Data,
            boundaryTimestamp,
            transportTimestamp);

    private static class EnumTextCache<TEnum> where TEnum : struct, Enum
    {
        private static readonly ConcurrentDictionary<TEnum, string> Values = [];

        public static string Get(TEnum value)
            => Values.GetOrAdd(value, static candidate => candidate.ToString());
    }
}
