using System.Runtime.InteropServices;

namespace DvmConsole.Vocoder;

// Owns ABI symbol resolution and pins caller-owned spans only for the duration
// of each native call. No protocol operation allocates a marshalling buffer.
internal sealed unsafe class NativeVocoderApi : IDisposable
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EncodeDelegate(IntPtr session, short* samples, nuint sampleCount, byte* output, nuint outputCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EncodeP25SingleToneDelegate(IntPtr session, double frequencyHz, byte* output, nuint outputCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FlushDelegate(IntPtr session, byte* output, nuint outputCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DecodeDelegate(IntPtr session, byte* input, nuint inputLength, short* samples, nuint sampleCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DecodeLostDelegate(IntPtr session, short* samples, nuint sampleCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DecodeParametersDelegate(
        IntPtr session,
        byte* parameters,
        nuint parameterLength,
        uint correctedErrors,
        [MarshalAs(UnmanagedType.I1)] bool lost,
        short* samples,
        nuint sampleCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ExtractDelegate(uint mode, byte* codeword, nuint codewordLength, byte* parameters, nuint parameterCapacity, out ushort correctedErrors);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BuildDelegate(uint mode, byte* parameters, nuint parameterLength, byte* codeword, nuint codewordCapacity);

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
    private readonly EncodeDelegate encodeParameters;
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
        encodeParameters = Get<EncodeDelegate>("dvmconsole_vocoder_encode_parameters");
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

    public SafeVocoderSessionHandle CreateSession(VocoderMode mode)
    {
        AddReference();
        IntPtr nativeHandle = create((uint)mode);
        if (nativeHandle == IntPtr.Zero)
        {
            ReleaseReference();
            return new SafeVocoderSessionHandle();
        }
        return new SafeVocoderSessionHandle(this, nativeHandle);
    }

    public int ConfigureReceiveAudioProcessing(SafeVocoderSessionHandle session, ReceiveAudioProcessingOptions options)
    {
        using var lease = new SafeVocoderSessionLease(session);
        return configureReceiveAudioProcessing(
            lease.Handle,
            options.HighPassFilterEnabled,
            options.HighPassFrequencyHz,
            options.PeakingFilterEnabled,
            options.PeakingFrequencyHz,
            options.PeakingGainDb,
            options.CompressorEnabled,
            options.CompressorRatio,
            options.CompressorThresholdDbfs,
            options.CompressorMakeupGainDb);
    }

    public int ResetSession(SafeVocoderSessionHandle session)
    {
        using var lease = new SafeVocoderSessionLease(session);
        return reset(lease.Handle);
    }

    public int Encode(SafeVocoderSessionHandle session, ReadOnlySpan<short> samples, Span<byte> output)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (short* samplesPointer = samples)
        fixed (byte* outputPointer = output)
            return encode(lease.Handle, samplesPointer, (nuint)samples.Length, outputPointer, (nuint)output.Length);
    }

    public int EncodeP25SingleTone(SafeVocoderSessionHandle session, double frequencyHz, Span<byte> output)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (byte* outputPointer = output)
            return encodeP25SingleTone(lease.Handle, frequencyHz, outputPointer, (nuint)output.Length);
    }

    public int FlushEncode(SafeVocoderSessionHandle session, Span<byte> output)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (byte* outputPointer = output)
            return flush(lease.Handle, outputPointer, (nuint)output.Length);
    }

    public int Decode(SafeVocoderSessionHandle session, ReadOnlySpan<byte> input, Span<short> samples)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (byte* inputPointer = input)
        fixed (short* samplesPointer = samples)
            return decode(lease.Handle, inputPointer, (nuint)input.Length, samplesPointer, (nuint)samples.Length);
    }

    public int DecodeLost(SafeVocoderSessionHandle session, Span<short> samples)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (short* samplesPointer = samples)
            return decodeLost(lease.Handle, samplesPointer, (nuint)samples.Length);
    }

    public int EncodeParameters(SafeVocoderSessionHandle session, ReadOnlySpan<short> samples, Span<byte> output)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (short* samplesPointer = samples)
        fixed (byte* outputPointer = output)
            return encodeParameters(lease.Handle, samplesPointer, (nuint)samples.Length, outputPointer, (nuint)output.Length);
    }

    public int FlushParameters(SafeVocoderSessionHandle session, Span<byte> output)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (byte* outputPointer = output)
            return flushParameters(lease.Handle, outputPointer, (nuint)output.Length);
    }

    public int DecodeParameters(SafeVocoderSessionHandle session, ReadOnlySpan<byte> parameters, uint correctedErrors, bool lost, Span<short> samples)
    {
        using var lease = new SafeVocoderSessionLease(session);
        fixed (byte* parametersPointer = parameters)
        fixed (short* samplesPointer = samples)
            return decodeParameters(lease.Handle, parametersPointer, (nuint)parameters.Length, correctedErrors, lost, samplesPointer, (nuint)samples.Length);
    }

    public int ExtractParameters(VocoderMode mode, ReadOnlySpan<byte> codeword, Span<byte> parameters, out ushort correctedErrors)
    {
        fixed (byte* codewordPointer = codeword)
        fixed (byte* parametersPointer = parameters)
            return extract((uint)mode, codewordPointer, (nuint)codeword.Length, parametersPointer, (nuint)parameters.Length, out correctedErrors);
    }

    public int BuildCodeword(VocoderMode mode, ReadOnlySpan<byte> parameters, Span<byte> codeword)
    {
        fixed (byte* parametersPointer = parameters)
        fixed (byte* codewordPointer = codeword)
            return build((uint)mode, parametersPointer, (nuint)parameters.Length, codewordPointer, (nuint)codeword.Length);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref ownerDisposed, 1) == 0)
            ReleaseReference();
    }

    internal void DestroySession(IntPtr session) => destroy(session);

    internal void ReleaseReference()
    {
        if (Interlocked.Decrement(ref referenceCount) == 0)
            NativeLibrary.Free(libraryHandle);
    }

    private void AddReference()
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

internal ref struct SafeVocoderSessionLease
{
    private SafeVocoderSessionHandle? session;
    private bool addedReference;

    internal SafeVocoderSessionLease(SafeVocoderSessionHandle session)
    {
        this.session = session;
        addedReference = false;
        session.DangerousAddRef(ref addedReference);
        Handle = session.DangerousGetHandle();
    }

    internal IntPtr Handle { get; }

    public void Dispose()
    {
        if (!addedReference)
            return;
        addedReference = false;
        session!.DangerousRelease();
        session = null;
    }
}

internal sealed class SafeVocoderSessionHandle : SafeHandle
{
    private NativeVocoderApi? owner;

    internal SafeVocoderSessionHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    internal SafeVocoderSessionHandle(NativeVocoderApi owner, IntPtr handle)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        this.owner = owner;
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeVocoderApi? currentOwner = owner;
        owner = null;
        if (currentOwner is null)
            return true;

        try
        {
            currentOwner.DestroySession(handle);
            return true;
        }
        finally
        {
            currentOwner.ReleaseReference();
        }
    }
}
