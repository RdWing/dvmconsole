namespace DvmConsole.Vocoder;

// Required built-in software vocoder. The native library is produced by the
// repository's Cargo build and ships with every runnable Avalonia package.
public sealed class SoftwareVocoderBackend : IVocoderBackend
{
    private readonly NativeVocoderApi api;
    private readonly IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions> receiveAudioProcessingOptions;
    private bool disposed;

    public SoftwareVocoderBackend(
        IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>? receiveAudioProcessingOptions = null)
    {
        this.receiveAudioProcessingOptions = Enum.GetValues<VocoderMode>()
            .ToDictionary(
                mode => mode,
                mode => receiveAudioProcessingOptions?.TryGetValue(mode, out ReceiveAudioProcessingOptions? options) == true
                    ? Validate(options)
                    : new ReceiveAudioProcessingOptions());
        api = NativeVocoderApi.Load();
    }

    public string Name => "Built-in software vocoder";
    public bool IsAvailable => true;

    public IVocoderSession CreateSession(VocoderMode mode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!api.Supports(mode))
            throw new NotSupportedException($"The built-in vocoder does not support {mode}.");
        return new SoftwareVocoderSession(api, mode, receiveAudioProcessingOptions[mode]);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        api.Dispose();
        disposed = true;
    }

    private static ReceiveAudioProcessingOptions Validate(ReceiveAudioProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options;
    }

    private sealed class SoftwareVocoderSession : IHalfRateVocoderSession, IP25GeneratedToneVocoderSession
    {
        private readonly NativeVocoderApi api;
        private readonly VocoderMode mode;
        private SafeVocoderSessionHandle? handle;

        public SoftwareVocoderSession(
            NativeVocoderApi api,
            VocoderMode mode,
            ReceiveAudioProcessingOptions receiveAudioProcessingOptions)
        {
            this.api = api;
            this.mode = mode;
            try
            {
                handle = api.CreateSession(mode);
                if (handle.IsInvalid)
                    throw Failure("create");
                if (api.ConfigureReceiveAudioProcessing(handle, receiveAudioProcessingOptions) < 0)
                    throw Failure("receive-processing configuration");
            }
            catch
            {
                handle?.Dispose();
                handle = null;
                throw;
            }
        }

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            ValidateFrame(samples.Length, codeword.Length);
            int result = api.Encode(SessionHandle, samples, codeword);
            RequireLength(result, codeword.Length, "encode");
            return result;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            ValidateFrame(samples.Length, codeword.Length);
            int result = api.Decode(SessionHandle, codeword, samples);
            if (result < 0)
                throw Failure("decode");
            return result;
        }

        public int DecodeLost(Span<short> samples)
        {
            ThrowIfDisposed();
            if (samples.Length != VocoderFrameSizes.PcmSamplesPerFrame)
                throw new ArgumentException($"A vocoder frame requires {VocoderFrameSizes.PcmSamplesPerFrame} PCM samples.", nameof(samples));
            int result = api.DecodeLost(SessionHandle, samples);
            if (result < 0)
                throw Failure("lost-frame decode");
            return result;
        }

        public int FlushEncode(Span<byte> codeword)
        {
            ThrowIfDisposed();
            int expected = VocoderFrameSizes.CodewordBytes(mode);
            if (codeword.Length != expected)
                throw new ArgumentException($"A {mode} vocoder codeword must be {expected} bytes.", nameof(codeword));

            int result = api.FlushEncode(SessionHandle, codeword);
            if (result < 0)
                throw Failure("flush");
            if (result == 0)
                return 0;
            RequireLength(result, expected, "flush");
            return result;
        }

        public int EncodeSingleTone(double frequencyHz, Span<byte> codeword)
        {
            ValidateP25GeneratedCodeword(codeword.Length);
            int result = api.EncodeP25SingleTone(SessionHandle, frequencyHz, codeword);
            RequireLength(result, codeword.Length, "P25 single-tone lookup");
            return result;
        }

        public void Reset()
        {
            ThrowIfDisposed();
            int result = api.ResetSession(SessionHandle);
            if (result < 0)
                throw Failure("reset");
        }

        public int EncodeParameters(ReadOnlySpan<short> samples, Span<byte> parameters)
        {
            ValidateHalfRateParameters(samples.Length, parameters.Length);
            int result = api.EncodeParameters(SessionHandle, samples, parameters);
            RequireLength(result, parameters.Length, "parameter encode");
            return result;
        }

        public int DecodeParameters(
            ReadOnlySpan<byte> parameters,
            Span<short> samples,
            uint correctedErrors = 0,
            bool lost = false)
        {
            ValidateHalfRateParameters(samples.Length, parameters.Length);
            int result = api.DecodeParameters(
                SessionHandle,
                parameters,
                correctedErrors,
                lost,
                samples);
            if (result < 0)
                throw Failure("parameter decode");
            return result;
        }

        public int FlushEncodeParameters(Span<byte> parameters)
        {
            ThrowIfDisposed();
            EnsureHalfRate();
            if (parameters.Length != VocoderFrameSizes.HalfRateParameterBytes)
                throw new ArgumentException("A half-rate parameter frame must be seven bytes.", nameof(parameters));

            int result = api.FlushParameters(SessionHandle, parameters);
            if (result < 0)
                throw Failure("parameter flush");
            if (result == 0)
                return 0;
            RequireLength(result, parameters.Length, "parameter flush");
            return result;
        }

        public int ExtractParameters(ReadOnlySpan<byte> codeword, Span<byte> parameters)
        {
            ThrowIfDisposed();
            EnsureHalfRate();
            if (codeword.Length != VocoderFrameSizes.HalfRateCodewordBytes)
                throw new ArgumentException("A half-rate codeword must be nine bytes.", nameof(codeword));
            if (parameters.Length != VocoderFrameSizes.HalfRateParameterBytes)
                throw new ArgumentException("A half-rate parameter frame must be seven bytes.", nameof(parameters));

            int result = api.ExtractParameters(mode, codeword, parameters, out ushort correctedErrors);
            RequireLength(result, parameters.Length, "FEC decode");
            return correctedErrors;
        }

        public HalfRateFecStatus ExtractParametersWithStatus(
            ReadOnlySpan<byte> codeword,
            Span<byte> parameters)
            => HalfRateFecStatus.FromNative(ExtractParameters(codeword, parameters));

        public void BuildCodeword(ReadOnlySpan<byte> parameters, Span<byte> codeword)
        {
            ThrowIfDisposed();
            EnsureHalfRate();
            if (parameters.Length != VocoderFrameSizes.HalfRateParameterBytes)
                throw new ArgumentException("A half-rate parameter frame must be seven bytes.", nameof(parameters));
            if (codeword.Length != VocoderFrameSizes.HalfRateCodewordBytes)
                throw new ArgumentException("A half-rate codeword must be nine bytes.", nameof(codeword));

            int result = api.BuildCodeword(mode, parameters, codeword);
            RequireLength(result, codeword.Length, "FEC encode");
        }

        public void Dispose()
        {
            if (handle is null)
                return;
            handle.Dispose();
            handle = null;
        }

        private void ValidateFrame(int sampleCount, int codewordLength)
        {
            ThrowIfDisposed();
            if (sampleCount != VocoderFrameSizes.PcmSamplesPerFrame)
                throw new ArgumentException($"A vocoder frame requires {VocoderFrameSizes.PcmSamplesPerFrame} PCM samples.", nameof(sampleCount));
            int expected = VocoderFrameSizes.CodewordBytes(mode);
            if (codewordLength != expected)
                throw new ArgumentException($"A {mode} vocoder codeword must be {expected} bytes.", nameof(codewordLength));
        }

        private void ValidateHalfRateParameters(int sampleCount, int parameterLength)
        {
            ThrowIfDisposed();
            EnsureHalfRate();
            if (sampleCount != VocoderFrameSizes.PcmSamplesPerFrame)
                throw new ArgumentException($"A vocoder frame requires {VocoderFrameSizes.PcmSamplesPerFrame} PCM samples.", nameof(sampleCount));
            if (parameterLength != VocoderFrameSizes.HalfRateParameterBytes)
                throw new ArgumentException("A half-rate parameter frame must be seven bytes.", nameof(parameterLength));
        }

        private void EnsureHalfRate()
        {
            if (mode is not (VocoderMode.DmrAmbe or VocoderMode.NxdnAmbe or VocoderMode.P25Phase2Ambe))
                throw new NotSupportedException("Parameter access is available only for half-rate sessions.");
        }

        private void ValidateP25GeneratedCodeword(int codewordLength)
        {
            ThrowIfDisposed();
            if (mode != VocoderMode.P25Imbe)
                throw new NotSupportedException("Generated P25 lookup frames require a P25 Phase 1 session.");
            if (codewordLength != VocoderFrameSizes.CodewordBytes(VocoderMode.P25Imbe))
                throw new ArgumentException("A P25 Phase 1 codeword must be 11 bytes.", nameof(codewordLength));
        }

        private void ThrowIfDisposed()
        {
            if (handle is null || handle.IsClosed)
                throw new ObjectDisposedException(nameof(SoftwareVocoderSession));
        }

        private SafeVocoderSessionHandle SessionHandle
        {
            get
            {
                ThrowIfDisposed();
                return handle!;
            }
        }

        private void RequireLength(int result, int expected, string operation)
        {
            if (result < 0)
                throw Failure(operation);
            if (result != expected)
                throw new InvalidOperationException($"The vocoder returned {result} bytes for {operation}; expected {expected}.");
        }

        private InvalidOperationException Failure(string operation)
            => new($"Built-in vocoder {operation} failed: {api.LastError ?? "unknown native error"}");
    }
}
