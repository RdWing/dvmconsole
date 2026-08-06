// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Runtime.InteropServices;
using DvmConsole.Platform.Native;

namespace DvmConsole.Platform.Audio.Vocoder
{
    /// <summary>
    /// Production <see cref="ILibVocoderNative"/> over the real 8-export
    /// libvocoder C ABI. The library is resolved exactly once, at
    /// construction: on macOS the packaged bundle candidate is preferred
    /// (<see cref="MacBundleLibraryResolver.ResolveLibraryPath(string?, string?, bool)"/>)
    /// with a fallback to the logical name <c>libvocoder</c> under the
    /// OS's own loading rules, then each export is resolved and cached as
    /// a Cdecl function pointer. Safe to construct on any host: off macOS
    /// no native library is ever resolved or loaded and every C-ABI call
    /// throws <see cref="PlatformNotSupportedException"/>. The library
    /// handle is released by <see cref="Dispose"/> (idempotent).
    /// </summary>
    public sealed class LibVocoderNative : ILibVocoderNative, IDisposable
    {
        /*
        ** Fields
        */

        private static readonly string NotMacOsMessage =
            "The libvocoder native library is only available on macOS; no native call can be made on this host.";

        private readonly object gate = new();
        private readonly LibVocoderExports.EncoderCreate? encoderCreate;
        private readonly LibVocoderExports.EncoderEncode? encoderEncode;
        private readonly LibVocoderExports.EncoderEncodeBits? encoderEncodeBits;
        private readonly LibVocoderExports.EncoderDelete? encoderDelete;
        private readonly LibVocoderExports.DecoderCreate? decoderCreate;
        private readonly LibVocoderExports.DecoderDecode? decoderDecode;
        private readonly LibVocoderExports.DecoderDecodeBits? decoderDecodeBits;
        private readonly LibVocoderExports.DecoderDelete? decoderDelete;
        private IntPtr library;
        private bool disposed;

        /*
        ** Constructors
        */

        /// <summary>
        /// Resolves the load path once and loads the library, resolving
        /// and caching every export as a Cdecl delegate. Off macOS this
        /// is a no-op: no native library is resolved or loaded and every
        /// C-ABI call throws <see cref="PlatformNotSupportedException"/>,
        /// so the seam is safe to construct anywhere.
        /// </summary>
        /// <exception cref="InvalidOperationException">On macOS when the
        /// library cannot be loaded.</exception>
        /// <exception cref="EntryPointNotFoundException">On macOS when a
        /// required export is missing.</exception>
        public LibVocoderNative()
        {
            var isMacOS = OperatingSystem.IsMacOS();
            if (!isMacOS)
            {
                return;
            }

            var loadName = MacBundleLibraryResolver.ResolveLibraryPath(
                    VocoderReadiness.LogicalLibraryName,
                    AppContext.BaseDirectory,
                    isMacOS)
                ?? VocoderReadiness.LogicalLibraryName;

            if (!NativeLibrary.TryLoad(loadName, out library))
            {
                throw new InvalidOperationException(
                    $"The {VocoderReadiness.LogicalLibraryName} native library could not be loaded ('{loadName}').");
            }

            try
            {
                encoderCreate = GetExport<LibVocoderExports.EncoderCreate>("MBEEncoder_Create");
                encoderEncode = GetExport<LibVocoderExports.EncoderEncode>("MBEEncoder_Encode");
                encoderEncodeBits = GetExport<LibVocoderExports.EncoderEncodeBits>("MBEEncoder_EncodeBits");
                encoderDelete = GetExport<LibVocoderExports.EncoderDelete>("MBEEncoder_Delete");
                decoderCreate = GetExport<LibVocoderExports.DecoderCreate>("MBEDecoder_Create");
                decoderDecode = GetExport<LibVocoderExports.DecoderDecode>("MBEDecoder_Decode");
                decoderDecodeBits = GetExport<LibVocoderExports.DecoderDecodeBits>("MBEDecoder_DecodeBits");
                decoderDelete = GetExport<LibVocoderExports.DecoderDelete>("MBEDecoder_Delete");
            }
            catch
            {
                // Do not leak the loaded library when export resolution
                // fails part-way (probe parity: the readiness probe also
                // frees its handle when an export is missing).
                NativeLibrary.Free(library);
                library = IntPtr.Zero;
                throw;
            }
        }

        /*
        ** Methods
        */

        /// <inheritdoc />
        public IntPtr MBEEncoder_Create(VocoderMode mode) => Require(encoderCreate)(mode);

        /// <inheritdoc />
        public void MBEEncoder_Encode(IntPtr handle, short[] samples, byte[] codeword)
            => Require(encoderEncode)(handle, samples, codeword);

        /// <inheritdoc />
        public void MBEEncoder_EncodeBits(IntPtr handle, byte[] bits, byte[] codeword)
            => Require(encoderEncodeBits)(handle, bits, codeword);

        /// <inheritdoc />
        public void MBEEncoder_Delete(IntPtr handle) => Require(encoderDelete)(handle);

        /// <inheritdoc />
        public IntPtr MBEDecoder_Create(VocoderMode mode) => Require(decoderCreate)(mode);

        /// <inheritdoc />
        public int MBEDecoder_Decode(IntPtr handle, byte[] codeword, short[] samples)
            => Require(decoderDecode)(handle, codeword, samples);

        /// <inheritdoc />
        public int MBEDecoder_DecodeBits(IntPtr handle, byte[] bits, byte[] codeword)
            => Require(decoderDecodeBits)(handle, bits, codeword);

        /// <inheritdoc />
        public void MBEDecoder_Delete(IntPtr handle) => Require(decoderDelete)(handle);

        /// <summary>
        /// Releases the loaded native library. Idempotent and thread-safe:
        /// concurrent calls serialize on an internal gate and the handle is
        /// freed exactly once.
        /// </summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (library != IntPtr.Zero)
                {
                    NativeLibrary.Free(library);
                    library = IntPtr.Zero;
                }
            }
        }

        /*
        ** Helpers
        */

        /// <summary>
        /// Returns the cached export delegate, or throws
        /// <see cref="PlatformNotSupportedException"/> when the seam was
        /// constructed off macOS and no native library was ever resolved.
        /// </summary>
        private static T Require<T>(T? fn) where T : Delegate
            => fn ?? throw new PlatformNotSupportedException(NotMacOsMessage);

        private T GetExport<T>(string name) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }

    /// <summary>
    /// Cdecl delegate types for the 8 libvocoder C ABI exports, matching
    /// the WPF DllImport signatures (VocoderInterop.cs). Held in a
    /// dedicated static holder class (CoreAudioNative convention) and
    /// instantiated via
    /// <see cref="Marshal.GetDelegateForFunctionPointer{T}(IntPtr)"/>.
    /// </summary>
    internal static class LibVocoderExports
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr EncoderCreate(VocoderMode mode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void EncoderEncode(IntPtr handle, short[] samples, byte[] codeword);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void EncoderEncodeBits(IntPtr handle, byte[] bits, byte[] codeword);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void EncoderDelete(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DecoderCreate(VocoderMode mode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int DecoderDecode(IntPtr handle, byte[] codeword, short[] samples);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int DecoderDecodeBits(IntPtr handle, byte[] bits, byte[] codeword);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void DecoderDelete(IntPtr handle);
    }
}
