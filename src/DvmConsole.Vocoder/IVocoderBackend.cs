namespace DvmConsole.Vocoder;

public enum VocoderMode
{
    DmrAmbe = 0,
    P25Imbe = 1
}

public static class VocoderFrameSizes
{
    public const int PcmSamplesPerFrame = 160;

    public static int CodewordBytes(VocoderMode mode)
    {
        return mode switch
        {
            VocoderMode.DmrAmbe => 9,
            VocoderMode.P25Imbe => 11,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported vocoder mode.")
        };
    }
}

public interface IVocoderBackend : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    IVocoderSession CreateSession(VocoderMode mode);
}

public interface IVocoderSession : IDisposable
{
    int Encode(ReadOnlySpan<short> samples, Span<byte> codeword);
    int Decode(ReadOnlySpan<byte> codeword, Span<short> samples);
}
