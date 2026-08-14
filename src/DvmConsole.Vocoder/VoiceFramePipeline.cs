namespace DvmConsole.Vocoder;

/// <summary>
/// Converts arbitrary PCM callback chunks into fixed-size vocoder codewords.
/// This is the seam used by future FNE transmit and receive routers.
/// </summary>
public sealed class VoiceFrameEncoder : IDisposable
{
    private readonly IVocoderSession session;
    private readonly VocoderMode mode;
    private readonly List<short> pending = [];
    private bool disposed;

    public VoiceFrameEncoder(IVocoderSession session, VocoderMode mode)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.mode = mode;
    }

    public int Process(ReadOnlySpan<short> samples, Action<ReadOnlyMemory<byte>> emit)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(emit);

        pending.AddRange(samples.ToArray());
        int emitted = 0;
        while (pending.Count >= VocoderFrameSizes.PcmSamplesPerFrame)
        {
            short[] frame = pending.GetRange(0, VocoderFrameSizes.PcmSamplesPerFrame).ToArray();
            pending.RemoveRange(0, VocoderFrameSizes.PcmSamplesPerFrame);
            byte[] codeword = new byte[VocoderFrameSizes.CodewordBytes(mode)];
            session.Encode(frame, codeword);
            emit(codeword);
            emitted++;
        }

        return emitted;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        session.Dispose();
        disposed = true;
    }

}

/// <summary>
/// Decodes one fixed-size FNE vocoder codeword into a PCM frame.
/// </summary>
public sealed class VoiceFrameDecoder : IDisposable
{
    private readonly IVocoderSession session;
    private readonly VocoderMode mode;
    private bool disposed;

    public VoiceFrameDecoder(IVocoderSession session, VocoderMode mode)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.mode = mode;
    }

    public int Process(ReadOnlySpan<byte> codeword, Action<ReadOnlyMemory<short>> emit)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(emit);
        if (codeword.Length != VocoderFrameSizes.CodewordBytes(mode))
            throw new ArgumentException("The codeword length does not match the decoder mode.", nameof(codeword));

        short[] samples = new short[VocoderFrameSizes.PcmSamplesPerFrame];
        int errors = session.Decode(codeword, samples);
        emit(samples);
        return errors;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        session.Dispose();
        disposed = true;
    }
}
