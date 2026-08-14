namespace DvmConsole.Vocoder;

public enum VocoderMode
{
    DmrAmbe = 0,
    P25Imbe = 1
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
