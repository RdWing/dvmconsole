namespace DvmConsole.Vocoder;

// Optional NXDN codec boundary. The default software backend does not
// implement this interface because NXDN requires its own FEC/AMBE+2 path.
public interface INxdnVocoderBackend : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    INxdnVocoderSession CreateSession();
}

public interface INxdnVocoderSession : IDisposable
{
    int Decode(ReadOnlySpan<byte> frame, Span<short> samples);
}
