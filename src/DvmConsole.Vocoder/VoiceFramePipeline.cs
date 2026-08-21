namespace DvmConsole.Vocoder;

// Converts arbitrary PCM callback chunks into fixed-size vocoder codewords.
// This is the seam used by future FNE transmit and receive routers.
public sealed class VoiceFrameEncoder : IDisposable
{
    private readonly IVocoderSession session;
    private readonly VocoderMode mode;
    private readonly List<short> pending = [];
    private bool hasUnflushedFrame;
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
            hasUnflushedFrame = true;
            emitted++;
        }

        return emitted;
    }

    // Emits the frame retained by a delayed encoder. Callers must pad any
    // incomplete PCM frame before flushing; an empty stream does not invent a
    // voice frame. A second flush is a no-op until more complete PCM arrives.
    public int Flush(Action<ReadOnlyMemory<byte>> emit)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(emit);
        if (pending.Count != 0)
            throw new InvalidOperationException("Pad the incomplete PCM frame before flushing the vocoder.");
        if (!hasUnflushedFrame)
            return 0;

        byte[] codeword = new byte[VocoderFrameSizes.CodewordBytes(mode)];
        int encoded = session.FlushEncode(codeword);
        hasUnflushedFrame = false;
        if (encoded == 0)
            return 0;
        if (encoded != codeword.Length)
            throw new InvalidOperationException("The vocoder returned an invalid flushed codeword length.");

        emit(codeword);
        return 1;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        session.Dispose();
        disposed = true;
    }

}

// Decodes one fixed-size FNE vocoder codeword into a PCM frame.
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

    public int Process(ReadOnlySpan<byte> codeword, Span<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (codeword.Length != VocoderFrameSizes.CodewordBytes(mode))
            throw new ArgumentException("The codeword length does not match the decoder mode.", nameof(codeword));
        if (samples.Length != VocoderFrameSizes.PcmSamplesPerFrame)
            throw new ArgumentException("The PCM destination must contain exactly one vocoder frame.", nameof(samples));

        return session.Decode(codeword, samples);
    }

    public int ProcessLost(Action<ReadOnlyMemory<short>> emit)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(emit);
        short[] samples = new short[VocoderFrameSizes.PcmSamplesPerFrame];
        int errors = session.DecodeLost(samples);
        emit(samples);
        return errors;
    }

    public int ProcessLost(Span<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (samples.Length != VocoderFrameSizes.PcmSamplesPerFrame)
            throw new ArgumentException("The PCM destination must contain exactly one vocoder frame.", nameof(samples));

        return session.DecodeLost(samples);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        session.Reset();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        session.Dispose();
        disposed = true;
    }
}
