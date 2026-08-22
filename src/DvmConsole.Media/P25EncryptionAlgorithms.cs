using fnecore.P25;

namespace DvmConsole.Media;

public static class P25EncryptionAlgorithms
{
    public const byte Unencrypted = P25Defines.P25_ALGO_UNENCRYPT;
    public const byte Aes = P25Defines.P25_ALGO_AES;
    public const byte Des = P25Defines.P25_ALGO_DES;
    public const byte Arc4 = P25Defines.P25_ALGO_ARC4;
}
