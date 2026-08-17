namespace DvmConsole.Vocoder;

public enum VocoderMode
{
    DmrAmbe = 0,
    P25Imbe = 1,
    NxdnAmbe = 2,
    // Codec/FEC seam for future P25 Phase 2 transport integration. The FNE
    // TDMA burst, signalling, and system-context descrambler are not wired yet.
    P25Phase2Ambe = 3
}

public static class VocoderFrameSizes
{
    public const int PcmSamplesPerFrame = 160;
    public const int HalfRateParameterBytes = 7;
    public const int HalfRateCodewordBytes = 9;

    public static int CodewordBytes(VocoderMode mode)
    {
        return mode switch
        {
            VocoderMode.DmrAmbe => 9,
            VocoderMode.P25Imbe => 11,
            VocoderMode.NxdnAmbe => 9,
            VocoderMode.P25Phase2Ambe => 9,
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
    int DecodeLost(Span<short> samples)
    {
        samples.Clear();
        return 0;
    }
    int FlushEncode(Span<byte> codeword) => 0;
    void Reset()
    {
    }
}

public readonly record struct HalfRateFecStatus(uint CorrectedErrors, bool Unrecoverable)
{
    public const int NativeUnrecoverableMarker = ushort.MaxValue;

    public uint DecoderErrorMetric => Unrecoverable ? 15u : CorrectedErrors;

    public static HalfRateFecStatus FromNative(int result)
        => result == NativeUnrecoverableMarker
            ? new HalfRateFecStatus(15, true)
            : new HalfRateFecStatus(checked((uint)result), false);
}

// Half-rate parameter access keeps DMR/NXDN and future P25 Phase 2 privacy
// between the vocoder and FEC. The seven-byte representation holds
// 49 significant bits followed by seven zero padding bits.
public interface IHalfRateVocoderSession : IVocoderSession
{
    int EncodeParameters(ReadOnlySpan<short> samples, Span<byte> parameters);
    int DecodeParameters(
        ReadOnlySpan<byte> parameters,
        Span<short> samples,
        uint correctedErrors = 0,
        bool lost = false);
    int FlushEncodeParameters(Span<byte> parameters);
    int ExtractParameters(ReadOnlySpan<byte> codeword, Span<byte> parameters);
    HalfRateFecStatus ExtractParametersWithStatus(
        ReadOnlySpan<byte> codeword,
        Span<byte> parameters)
        => HalfRateFecStatus.FromNative(ExtractParameters(codeword, parameters));
    void BuildCodeword(ReadOnlySpan<byte> parameters, Span<byte> codeword);
}
