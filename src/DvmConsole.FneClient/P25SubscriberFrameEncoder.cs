using fnecore;
using fnecore.EDAC;
using fnecore.P25;

namespace DvmConsole.FneClient;

internal static class P25SubscriberFrameEncoder
{
    public static byte[] Encode(P25SubscriberCommandMessage message, RemoteCallData callData)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(callData);

        byte[] payload = new byte[200];
        FneUtils.StringToBytes(Constants.TAG_P25_DATA, payload, 0, Constants.TAG_P25_DATA.Length);
        payload[4] = callData.LCO;
        FneUtils.Write3Bytes(callData.SrcId, ref payload, 5);
        FneUtils.Write3Bytes(callData.DstId, ref payload, 8);
        payload[11] = 0;
        payload[12] = 0;
        payload[14] = 0;
        payload[15] = callData.MFId;
        payload[16] = 0;
        payload[17] = 0;
        payload[18] = 0;
        payload[20] = callData.LSD1;
        payload[21] = callData.LSD2;
        payload[22] = (byte)P25DUID.TSDU;

        var trellis = new Trellis();
        byte[] tsbkTrellis = new byte[P25Defines.P25_TSBK_FEC_LENGTH_BYTES];
        trellis.Encode12(message.Tsbk, ref tsbkTrellis);
        byte[] raw = new byte[P25Defines.P25_TSDU_FRAME_LENGTH_BYTES];
        P25Interleaver.Encode(tsbkTrellis, ref raw, 114, 318);
        Buffer.BlockCopy(raw, 0, payload, 24, raw.Length);
        payload[23] = (byte)(24 + raw.Length);

        return payload;
    }
}
