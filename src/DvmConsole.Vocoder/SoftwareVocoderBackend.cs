using System.Runtime.InteropServices;

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
        private IntPtr handle;

        public SoftwareVocoderSession(
            NativeVocoderApi api,
            VocoderMode mode,
            ReceiveAudioProcessingOptions receiveAudioProcessingOptions)
        {
            this.api = api;
            this.mode = mode;
            api.AddReference();
            try
            {
                handle = api.CreateSession(mode);
                if (handle == IntPtr.Zero)
                    throw Failure("create");
                if (api.ConfigureReceiveAudioProcessing(handle, receiveAudioProcessingOptions) < 0)
                    throw Failure("receive-processing configuration");
            }
            catch
            {
                IntPtr createdHandle = handle;
                handle = IntPtr.Zero;
                try
                {
                    if (createdHandle != IntPtr.Zero)
                        api.DestroySession(createdHandle);
                }
                finally
                {
                    api.ReleaseReference();
                }
                throw;
            }
        }

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            ValidateFrame(samples.Length, codeword.Length);
            byte[] output = new byte[codeword.Length];
            int result = api.Encode(handle, samples.ToArray(), output);
            RequireLength(result, output.Length, "encode");
            output.CopyTo(codeword);
            return result;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            ValidateFrame(samples.Length, codeword.Length);
            short[] output = new short[samples.Length];
            int result = api.Decode(handle, codeword.ToArray(), output);
            if (result < 0)
                throw Failure("decode");
            output.CopyTo(samples);
            return result;
        }

        public int DecodeLost(Span<short> samples)
        {
            ThrowIfDisposed();
            if (samples.Length != VocoderFrameSizes.PcmSamplesPerFrame)
                throw new ArgumentException($"A vocoder frame requires {VocoderFrameSizes.PcmSamplesPerFrame} PCM samples.", nameof(samples));
            short[] output = new short[samples.Length];
            int result = api.DecodeLost(handle, output);
            if (result < 0)
                throw Failure("lost-frame decode");
            output.CopyTo(samples);
            return result;
        }

        public int FlushEncode(Span<byte> codeword)
        {
            ThrowIfDisposed();
            int expected = VocoderFrameSizes.CodewordBytes(mode);
            if (codeword.Length != expected)
                throw new ArgumentException($"A {mode} vocoder codeword must be {expected} bytes.", nameof(codeword));

            byte[] output = new byte[expected];
            int result = api.FlushEncode(handle, output);
            if (result < 0)
                throw Failure("flush");
            if (result == 0)
                return 0;
            RequireLength(result, expected, "flush");
            output.CopyTo(codeword);
            return result;
        }

        public int EncodeSingleTone(double frequencyHz, Span<byte> codeword)
        {
            ValidateP25GeneratedCodeword(codeword.Length);
            byte[] output = new byte[VocoderFrameSizes.CodewordBytes(VocoderMode.P25Imbe)];
            int result = api.EncodeP25SingleTone(handle, frequencyHz, output);
            RequireLength(result, output.Length, "P25 single-tone lookup");
            output.CopyTo(codeword);
            return result;
        }

        public void Reset()
        {
            ThrowIfDisposed();
            int result = api.ResetSession(handle);
            if (result < 0)
                throw Failure("reset");
        }

        public int EncodeParameters(ReadOnlySpan<short> samples, Span<byte> parameters)
        {
            ValidateHalfRateParameters(samples.Length, parameters.Length);
            byte[] output = new byte[VocoderFrameSizes.HalfRateParameterBytes];
            int result = api.EncodeParameters(handle, samples.ToArray(), output);
            RequireLength(result, output.Length, "parameter encode");
            output.CopyTo(parameters);
            return result;
        }

        public int DecodeParameters(
            ReadOnlySpan<byte> parameters,
            Span<short> samples,
            uint correctedErrors = 0,
            bool lost = false)
        {
            ValidateHalfRateParameters(samples.Length, parameters.Length);
            short[] output = new short[VocoderFrameSizes.PcmSamplesPerFrame];
            int result = api.DecodeParameters(
                handle,
                parameters.ToArray(),
                correctedErrors,
                lost,
                output);
            if (result < 0)
                throw Failure("parameter decode");
            output.CopyTo(samples);
            return result;
        }

        public int FlushEncodeParameters(Span<byte> parameters)
        {
            ThrowIfDisposed();
            EnsureHalfRate();
            if (parameters.Length != VocoderFrameSizes.HalfRateParameterBytes)
                throw new ArgumentException("A half-rate parameter frame must be seven bytes.", nameof(parameters));

            byte[] output = new byte[VocoderFrameSizes.HalfRateParameterBytes];
            int result = api.FlushParameters(handle, output);
            if (result < 0)
                throw Failure("parameter flush");
            if (result == 0)
                return 0;
            RequireLength(result, output.Length, "parameter flush");
            output.CopyTo(parameters);
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

            byte[] output = new byte[VocoderFrameSizes.HalfRateParameterBytes];
            int result = api.ExtractParameters(mode, codeword.ToArray(), output, out ushort correctedErrors);
            RequireLength(result, output.Length, "FEC decode");
            output.CopyTo(parameters);
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

            byte[] output = new byte[VocoderFrameSizes.HalfRateCodewordBytes];
            int result = api.BuildCodeword(mode, parameters.ToArray(), output);
            RequireLength(result, output.Length, "FEC encode");
            output.CopyTo(codeword);
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero)
                return;
            IntPtr sessionHandle = handle;
            handle = IntPtr.Zero;
            try
            {
                api.DestroySession(sessionHandle);
            }
            finally
            {
                api.ReleaseReference();
            }
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
            if (handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(SoftwareVocoderSession));
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

    private sealed class NativeVocoderApi : IDisposable
    {
        private const uint RequiredAbiVersion = 7;
        private const string LibraryBaseName = "dvmconsole_vocoder";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint VersionDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong CapabilitiesDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateDelegate(uint mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ConfigureReceiveAudioProcessingDelegate(
            IntPtr session,
            [MarshalAs(UnmanagedType.I1)] bool highPassEnabled,
            float highPassFrequencyHz,
            [MarshalAs(UnmanagedType.I1)] bool peakingEnabled,
            float peakingFrequencyHz,
            float peakingGainDb,
            [MarshalAs(UnmanagedType.I1)] bool compressorEnabled,
            float compressorRatio,
            float compressorThresholdDbfs,
            float compressorMakeupGainDb);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ResetDelegate(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EncodeDelegate(IntPtr session, short[] samples, nuint sampleCount, byte[] output, nuint outputCapacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EncodeP25SingleToneDelegate(IntPtr session, double frequencyHz, byte[] output, nuint outputCapacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FlushDelegate(IntPtr session, byte[] output, nuint outputCapacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DecodeDelegate(IntPtr session, byte[] input, nuint inputLength, short[] samples, nuint sampleCapacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DecodeLostDelegate(IntPtr session, short[] samples, nuint sampleCapacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EncodeParametersDelegate(IntPtr session, short[] samples, nuint sampleCount, byte[] output, nuint outputCapacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DecodeParametersDelegate(
            IntPtr session,
            byte[] parameters,
            nuint parameterLength,
            uint correctedErrors,
            [MarshalAs(UnmanagedType.I1)] bool lost,
            short[] samples,
            nuint sampleCapacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ExtractDelegate(uint mode, byte[] codeword, nuint codewordLength, byte[] parameters, nuint parameterCapacity, out ushort correctedErrors);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BuildDelegate(uint mode, byte[] parameters, nuint parameterLength, byte[] codeword, nuint codewordCapacity);

        private readonly IntPtr libraryHandle;
        private readonly ulong capabilities;
        private readonly ErrorDelegate error;
        private readonly CreateDelegate create;
        private readonly DestroyDelegate destroy;
        private readonly ConfigureReceiveAudioProcessingDelegate configureReceiveAudioProcessing;
        private readonly ResetDelegate reset;
        private readonly EncodeDelegate encode;
        private readonly EncodeP25SingleToneDelegate encodeP25SingleTone;
        private readonly FlushDelegate flush;
        private readonly DecodeDelegate decode;
        private readonly DecodeLostDelegate decodeLost;
        private readonly EncodeParametersDelegate encodeParameters;
        private readonly FlushDelegate flushParameters;
        private readonly DecodeParametersDelegate decodeParameters;
        private readonly ExtractDelegate extract;
        private readonly BuildDelegate build;
        private int referenceCount = 1;
        private int ownerDisposed;

        private NativeVocoderApi(IntPtr libraryHandle)
        {
            this.libraryHandle = libraryHandle;
            VersionDelegate version = Get<VersionDelegate>("dvmconsole_vocoder_abi_version");
            var getCapabilities = Get<CapabilitiesDelegate>("dvmconsole_vocoder_capabilities");
            error = Get<ErrorDelegate>("dvmconsole_vocoder_last_error");
            create = Get<CreateDelegate>("dvmconsole_vocoder_session_create");
            destroy = Get<DestroyDelegate>("dvmconsole_vocoder_session_destroy");
            configureReceiveAudioProcessing = Get<ConfigureReceiveAudioProcessingDelegate>(
                "dvmconsole_vocoder_configure_rx_audio_processing");
            reset = Get<ResetDelegate>("dvmconsole_vocoder_session_reset");
            encode = Get<EncodeDelegate>("dvmconsole_vocoder_encode");
            encodeP25SingleTone = Get<EncodeP25SingleToneDelegate>("dvmconsole_vocoder_encode_p25_single_tone");
            flush = Get<FlushDelegate>("dvmconsole_vocoder_flush_encode");
            decode = Get<DecodeDelegate>("dvmconsole_vocoder_decode");
            decodeLost = Get<DecodeLostDelegate>("dvmconsole_vocoder_decode_lost");
            encodeParameters = Get<EncodeParametersDelegate>("dvmconsole_vocoder_encode_parameters");
            flushParameters = Get<FlushDelegate>("dvmconsole_vocoder_flush_parameters");
            decodeParameters = Get<DecodeParametersDelegate>("dvmconsole_vocoder_decode_parameters");
            extract = Get<ExtractDelegate>("dvmconsole_vocoder_half_rate_extract");
            build = Get<BuildDelegate>("dvmconsole_vocoder_half_rate_build");

            uint abiVersion = version();
            if (abiVersion != RequiredAbiVersion)
                throw new InvalidOperationException($"Unsupported built-in vocoder ABI version {abiVersion}; expected {RequiredAbiVersion}.");
            capabilities = getCapabilities();
            if ((capabilities & 0b1111UL) != 0b1111UL)
                throw new InvalidOperationException("The built-in vocoder does not provide all required protocol modes.");
        }

        public string? LastError
        {
            get
            {
                IntPtr pointer = error();
                return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
            }
        }

        public static NativeVocoderApi Load()
        {
            IntPtr handle = LoadLibrary();
            try
            {
                return new NativeVocoderApi(handle);
            }
            catch
            {
                NativeLibrary.Free(handle);
                throw;
            }
        }

        public bool Supports(VocoderMode mode)
        {
            int bit = (int)mode;
            return bit is >= 0 and < 64 && (capabilities & (1UL << bit)) != 0;
        }

        public IntPtr CreateSession(VocoderMode mode) => create((uint)mode);
        public void AddReference()
        {
            while (true)
            {
                int current = Volatile.Read(ref referenceCount);
                if (current == 0)
                    throw new ObjectDisposedException(nameof(NativeVocoderApi));
                if (Interlocked.CompareExchange(ref referenceCount, current + 1, current) == current)
                    return;
            }
        }

        public void ReleaseReference()
        {
            if (Interlocked.Decrement(ref referenceCount) == 0)
                NativeLibrary.Free(libraryHandle);
        }
        public void DestroySession(IntPtr session) => destroy(session);
        public int ConfigureReceiveAudioProcessing(
            IntPtr session,
            ReceiveAudioProcessingOptions options)
            => configureReceiveAudioProcessing(
                session,
                options.HighPassFilterEnabled,
                options.HighPassFrequencyHz,
                options.PeakingFilterEnabled,
                options.PeakingFrequencyHz,
                options.PeakingGainDb,
                options.CompressorEnabled,
                options.CompressorRatio,
                options.CompressorThresholdDbfs,
                options.CompressorMakeupGainDb);
        public int ResetSession(IntPtr session) => reset(session);
        public int Encode(IntPtr session, short[] samples, byte[] output)
            => encode(session, samples, (nuint)samples.Length, output, (nuint)output.Length);
        public int EncodeP25SingleTone(IntPtr session, double frequencyHz, byte[] output)
            => encodeP25SingleTone(session, frequencyHz, output, (nuint)output.Length);
        public int FlushEncode(IntPtr session, byte[] output)
            => flush(session, output, (nuint)output.Length);
        public int Decode(IntPtr session, byte[] input, short[] samples)
            => decode(session, input, (nuint)input.Length, samples, (nuint)samples.Length);
        public int DecodeLost(IntPtr session, short[] samples)
            => decodeLost(session, samples, (nuint)samples.Length);
        public int EncodeParameters(IntPtr session, short[] samples, byte[] output)
            => encodeParameters(session, samples, (nuint)samples.Length, output, (nuint)output.Length);
        public int FlushParameters(IntPtr session, byte[] output)
            => flushParameters(session, output, (nuint)output.Length);
        public int DecodeParameters(IntPtr session, byte[] parameters, uint correctedErrors, bool lost, short[] samples)
            => decodeParameters(session, parameters, (nuint)parameters.Length, correctedErrors, lost, samples, (nuint)samples.Length);
        public int ExtractParameters(VocoderMode mode, byte[] codeword, byte[] parameters, out ushort correctedErrors)
            => extract((uint)mode, codeword, (nuint)codeword.Length, parameters, (nuint)parameters.Length, out correctedErrors);
        public int BuildCodeword(VocoderMode mode, byte[] parameters, byte[] codeword)
            => build((uint)mode, parameters, (nuint)parameters.Length, codeword, (nuint)codeword.Length);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ownerDisposed, 1) == 0)
                ReleaseReference();
        }

        private T Get<T>(string symbol) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(libraryHandle, symbol));

        private static IntPtr LoadLibrary()
        {
            string fileName = OperatingSystem.IsMacOS()
                ? "libdvmconsole_vocoder.dylib"
                : OperatingSystem.IsWindows()
                    ? "dvmconsole_vocoder.dll"
                    : "libdvmconsole_vocoder.so";
            string candidate = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(candidate))
                return NativeLibrary.Load(candidate);

            return NativeLibrary.Load(
                LibraryBaseName,
                typeof(SoftwareVocoderBackend).Assembly,
                DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories);
        }
    }
}
