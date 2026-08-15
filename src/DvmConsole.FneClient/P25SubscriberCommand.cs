using fnecore.P25;
using fnecore.P25.LC.TSBK;
using System.Globalization;

namespace DvmConsole.FneClient;

public enum P25SubscriberCommand
{
    CallAlert,
    RadioCheck,
    Inhibit,
    Uninhibit
}

public sealed record P25SubscriberCommandMessage(
    P25SubscriberCommand Command,
    uint SourceId,
    uint DestinationId,
    byte LinkControlOpcode,
    byte[] Tsbk);

/// <summary>
/// Builds the same P25 TSBK subscriber commands used by the WPF console while
/// keeping validation and packet construction testable without a live FNE.
/// </summary>
public static class P25SubscriberCommandCodec
{
    public const uint MaximumP25Id = 0xFFFFFF;

    public static bool IsValidSubscriberId(uint subscriberId)
        => subscriberId is > 0 and <= MaximumP25Id;

    public static bool TryParseSubscriberId(string? text, out uint subscriberId)
    {
        return uint.TryParse(
                   text?.Trim(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out subscriberId) &&
               IsValidSubscriberId(subscriberId);
    }

    public static P25SubscriberCommandMessage Build(
        P25SubscriberCommand command,
        uint sourceId,
        uint destinationId)
    {
        ValidateId(sourceId, nameof(sourceId));
        ValidateId(destinationId, nameof(destinationId));

        byte[] tsbk = new byte[P25Defines.P25_TSBK_LENGTH_BYTES];
        byte opcode;
        switch (command)
        {
            case P25SubscriberCommand.CallAlert:
                opcode = P25Defines.TSBK_IOSP_CALL_ALRT;
                new IOSP_CALL_ALRT(destinationId, sourceId).Encode(ref tsbk);
                break;
            case P25SubscriberCommand.RadioCheck:
                opcode = P25Defines.TSBK_IOSP_EXT_FNCT;
                new IOSP_EXT_FNCT((ushort)ExtendedFunction.CHECK, sourceId, destinationId).Encode(ref tsbk);
                break;
            case P25SubscriberCommand.Inhibit:
                opcode = P25Defines.TSBK_IOSP_EXT_FNCT;
                new IOSP_EXT_FNCT((ushort)ExtendedFunction.INHIBIT, P25Defines.WUID_FNE, destinationId).Encode(ref tsbk);
                break;
            case P25SubscriberCommand.Uninhibit:
                opcode = P25Defines.TSBK_IOSP_EXT_FNCT;
                new IOSP_EXT_FNCT((ushort)ExtendedFunction.UNINHIBIT, P25Defines.WUID_FNE, destinationId).Encode(ref tsbk);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }

        return new P25SubscriberCommandMessage(command, sourceId, destinationId, opcode, tsbk);
    }

    private static void ValidateId(uint value, string parameterName)
    {
        if (!IsValidSubscriberId(value))
            throw new ArgumentOutOfRangeException(parameterName, "P25 subscriber IDs must be between 1 and 16777215.");
    }
}
