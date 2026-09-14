// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;

namespace DvmConsole.Vocoder;

// Explicit host opt-in only. iOS resolves these C entry points in its executable.
internal static unsafe class StaticVocoderImports
{
    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint dvmconsole_vocoder_abi_version();

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern ulong dvmconsole_vocoder_capabilities();

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr dvmconsole_vocoder_last_error();

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr dvmconsole_vocoder_session_create(uint mode);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void dvmconsole_vocoder_session_destroy(IntPtr session);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_configure_rx_audio_processing(
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

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_defer_rx_processing(IntPtr session);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_process_rx_presentation(IntPtr session, short* samples, nuint sampleCapacity);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_session_reset(IntPtr session);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_encode(IntPtr session, short* samples, nuint sampleCount, byte* output, nuint outputCapacity);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_encode_p25_single_tone(IntPtr session, double frequencyHz, byte* output, nuint outputCapacity);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_flush_encode(IntPtr session, byte* output, nuint outputCapacity);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_decode(IntPtr session, byte* input, nuint inputLength, short* samples, nuint sampleCapacity);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_decode_lost(IntPtr session, short* samples, nuint sampleCapacity);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_encode_parameters(IntPtr session, short* samples, nuint sampleCount, byte* output, nuint outputCapacity);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_flush_parameters(IntPtr session, byte* output, nuint outputCapacity);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_decode_parameters(
        IntPtr session,
        byte* parameters,
        nuint parameterLength,
        uint correctedErrors,
        [MarshalAs(UnmanagedType.I1)] bool lost,
        short* samples,
        nuint sampleCapacity);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_half_rate_extract(uint mode, byte* codeword, nuint codewordLength, byte* parameters, nuint parameterCapacity, out ushort correctedErrors);

    [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int dvmconsole_vocoder_half_rate_build(uint mode, byte* parameters, nuint parameterLength, byte* codeword, nuint codewordCapacity);

}
