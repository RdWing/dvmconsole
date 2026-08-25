using fnecore;
using fnecore.DMR;

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
            args.CallType.ToString(),
            args.FrameType.ToString(),
            GetDmrSubtype(args),
            args.PacketSequence,
            args.StreamId,
            args.Data,
            boundaryTimestamp,
            transportTimestamp);

    private static string GetDmrSubtype(DMRDataReceivedEvent args)
    {
        if (args.Data.Length <= 15)
            return args.DataType.ToString();

        byte control = args.Data[15];
        return (control & 0x20) != 0
            ? ((DMRDataType)(control & 0x0F)).ToString()
            : args.DataType.ToString();
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
            args.CallType.ToString(),
            args.FrameType.ToString(),
            args.DUID.ToString(),
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
            args.CallType.ToString(),
            args.FrameType.ToString(),
            args.MessageType.ToString(),
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
            args.CallType.ToString(),
            args.FrameType.ToString(),
            args.AudioFrameType.ToString(),
            args.PacketSequence,
            args.StreamId,
            args.Data,
            boundaryTimestamp,
            transportTimestamp);
}
