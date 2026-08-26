namespace DvmConsole.Media;

// Implements the 64-stage NXDN IV generator shared by DES and AES. VCALL_IV
// carries the current 64-bit LFSR state; advancing it by 64 shifts produces
// the seed for the next eight-frame encryption session.
internal static class NxdnInitializationVectorGenerator
{
    public static byte[] GetNextSeed(ReadOnlySpan<byte> seed)
    {
        ulong state = ReadSeed(seed);
        for (int index = 0; index < 64; index++)
        {
            ulong feedback = ((state >> 63) ^
                (state >> 61) ^
                (state >> 45) ^
                (state >> 37) ^
                (state >> 26) ^
                (state >> 14)) & 1;
            state = (state << 1) | feedback;
        }
        return WriteSeed(state);
    }

    public static byte[] CreateAesInitializationVector(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != NxdnPrivacyAlgorithms.MessageIndicatorBytes)
            throw new ArgumentException("NXDN AES requires an 8-byte IV seed.", nameof(seed));

        byte[] initializationVector = new byte[16];
        seed.CopyTo(initializationVector);
        GetNextSeed(seed).CopyTo(initializationVector, seed.Length);
        return initializationVector;
    }

    private static ulong ReadSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != NxdnPrivacyAlgorithms.MessageIndicatorBytes)
            throw new ArgumentException("NXDN IV generation requires an 8-byte seed.", nameof(seed));

        ulong state = 0;
        foreach (byte value in seed)
            state = (state << 8) | value;
        return state;
    }

    private static byte[] WriteSeed(ulong state)
    {
        byte[] seed = new byte[NxdnPrivacyAlgorithms.MessageIndicatorBytes];
        for (int index = seed.Length - 1; index >= 0; index--)
        {
            seed[index] = (byte)state;
            state >>= 8;
        }
        return seed;
    }
}
