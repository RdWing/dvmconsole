// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;

namespace DvmConsole.Vocoder;

// Symbol binding is independent of managed session and safe-handle ownership.
internal sealed unsafe class NativeVocoderBindings : IDisposable
{
    private const string LibraryBaseName = "dvmconsole_vocoder";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint VersionDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate ulong CapabilitiesDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ErrorDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr CreateDelegate(uint mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DestroyDelegate(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ConfigureReceiveAudioProcessingDelegate(
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ResetDelegate(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int EncodeDelegate(IntPtr session, short* samples, nuint sampleCount, byte* output, nuint outputCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int EncodeP25SingleToneDelegate(IntPtr session, double frequencyHz, byte* output, nuint outputCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int FlushDelegate(IntPtr session, byte* output, nuint outputCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DecodeDelegate(IntPtr session, byte* input, nuint inputLength, short* samples, nuint sampleCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DecodeLostDelegate(IntPtr session, short* samples, nuint sampleCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DecodeParametersDelegate(
        IntPtr session,
        byte* parameters,
        nuint parameterLength,
        uint correctedErrors,
        [MarshalAs(UnmanagedType.I1)] bool lost,
        short* samples,
        nuint sampleCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ExtractDelegate(uint mode, byte* codeword, nuint codewordLength, byte* parameters, nuint parameterCapacity, out ushort correctedErrors);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int BuildDelegate(uint mode, byte* parameters, nuint parameterLength, byte* codeword, nuint codewordCapacity);

    private readonly IntPtr libraryHandle;
    internal readonly VersionDelegate version;
    internal readonly CapabilitiesDelegate getCapabilities;
    internal readonly ErrorDelegate error;
    internal readonly CreateDelegate create;
    internal readonly DestroyDelegate destroy;
    internal readonly ConfigureReceiveAudioProcessingDelegate configureReceiveAudioProcessing;
    internal readonly ResetDelegate reset;
    internal readonly ResetDelegate deferReceiveProcessing;
    internal readonly DecodeLostDelegate processReceivePresentation;
    internal readonly EncodeDelegate encode;
    internal readonly EncodeP25SingleToneDelegate encodeP25SingleTone;
    internal readonly FlushDelegate flush;
    internal readonly DecodeDelegate decode;
    internal readonly DecodeLostDelegate decodeLost;
    internal readonly EncodeDelegate encodeParameters;
    internal readonly FlushDelegate flushParameters;
    internal readonly DecodeParametersDelegate decodeParameters;
    internal readonly ExtractDelegate extract;
    internal readonly BuildDelegate build;

    private NativeVocoderBindings(IntPtr libraryHandle, bool staticallyLinked)
    {
        this.libraryHandle = libraryHandle;
        version = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_abi_version : Get<VersionDelegate>("dvmconsole_vocoder_abi_version");
        getCapabilities = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_capabilities : Get<CapabilitiesDelegate>("dvmconsole_vocoder_capabilities");
        error = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_last_error : Get<ErrorDelegate>("dvmconsole_vocoder_last_error");
        create = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_session_create : Get<CreateDelegate>("dvmconsole_vocoder_session_create");
        destroy = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_session_destroy : Get<DestroyDelegate>("dvmconsole_vocoder_session_destroy");
        configureReceiveAudioProcessing = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_configure_rx_audio_processing : Get<ConfigureReceiveAudioProcessingDelegate>("dvmconsole_vocoder_configure_rx_audio_processing");
        deferReceiveProcessing = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_defer_rx_processing : Get<ResetDelegate>("dvmconsole_vocoder_defer_rx_processing");
        processReceivePresentation = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_process_rx_presentation : Get<DecodeLostDelegate>("dvmconsole_vocoder_process_rx_presentation");
        reset = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_session_reset : Get<ResetDelegate>("dvmconsole_vocoder_session_reset");
        encode = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_encode : Get<EncodeDelegate>("dvmconsole_vocoder_encode");
        encodeP25SingleTone = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_encode_p25_single_tone : Get<EncodeP25SingleToneDelegate>("dvmconsole_vocoder_encode_p25_single_tone");
        flush = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_flush_encode : Get<FlushDelegate>("dvmconsole_vocoder_flush_encode");
        decode = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_decode : Get<DecodeDelegate>("dvmconsole_vocoder_decode");
        decodeLost = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_decode_lost : Get<DecodeLostDelegate>("dvmconsole_vocoder_decode_lost");
        encodeParameters = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_encode_parameters : Get<EncodeDelegate>("dvmconsole_vocoder_encode_parameters");
        flushParameters = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_flush_parameters : Get<FlushDelegate>("dvmconsole_vocoder_flush_parameters");
        decodeParameters = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_decode_parameters : Get<DecodeParametersDelegate>("dvmconsole_vocoder_decode_parameters");
        extract = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_half_rate_extract : Get<ExtractDelegate>("dvmconsole_vocoder_half_rate_extract");
        build = staticallyLinked ? StaticVocoderImports.dvmconsole_vocoder_half_rate_build : Get<BuildDelegate>("dvmconsole_vocoder_half_rate_build");

    }

    internal static NativeVocoderBindings Load(NativeVocoderLinkage linkage)
    {
        if (linkage == NativeVocoderLinkage.StaticallyLinked)
            return new NativeVocoderBindings(IntPtr.Zero, staticallyLinked: true);
        if (linkage != NativeVocoderLinkage.Dynamic)
            throw new ArgumentOutOfRangeException(nameof(linkage));
        IntPtr handle = LoadLibrary();
        try { return new NativeVocoderBindings(handle, staticallyLinked: false); }
        catch { NativeLibrary.Free(handle); throw; }
    }

    public void Dispose()
    {
        if (libraryHandle != IntPtr.Zero)
            NativeLibrary.Free(libraryHandle);
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
