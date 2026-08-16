using DvmConsole.Media;

namespace DvmConsole.Desktop;

// Redacted operator-facing encryption status.  It intentionally exposes
// algorithm and key identifiers but never key material.
public sealed record KeyStatusItemViewModel(
    string SystemName,
    string ChannelName,
    string ModeText,
    string AlgorithmIdText,
    string KeyIdText,
    string StatusText)
{
    public static KeyStatusItemViewModel From(
        ChannelViewModel channel,
        IP25KeyResolver? keyResolver)
    {
        ArgumentNullException.ThrowIfNull(channel);

        string algorithmText = P25KeyRing.TryParseAlgorithmId(
                channel.Definition.EncryptionAlgorithm,
                out byte algorithmId)
            ? $"0x{algorithmId:X2}"
            : channel.Definition.EncryptionAlgorithm;
        string keyIdText = P25KeyRing.TryParseKeyId(
                channel.Definition.EncryptionKeyId,
                out ushort keyId)
            ? $"0x{keyId:X4}"
            : channel.Definition.EncryptionKeyId ?? "—";
        string statusText = !channel.Definition.IsEncrypted
            ? "Clear"
            : channel.Definition.Mode != "p25"
                ? "Unsupported protocol"
                : keyResolver?.CanResolve(
                    channel.Definition.EncryptionAlgorithm,
                    channel.Definition.EncryptionKeyId) == true
                    ? "Key available"
                    : "Key unavailable";

        return new KeyStatusItemViewModel(
            channel.Definition.SystemName,
            channel.Name,
            channel.ModeText,
            algorithmText,
            keyIdText,
            statusText);
    }
}
