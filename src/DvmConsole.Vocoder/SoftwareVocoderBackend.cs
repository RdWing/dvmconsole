using System.Runtime.InteropServices;

namespace DvmConsole.Vocoder;

// Software vocoder backed by the native dvmvocoder shared library.
// The loader is explicit so macOS .dylib packaging and a future AMBE backend
// do not leak platform-specific filenames into the application core.
public sealed class SoftwareVocoderBackend : IVocoderBackend
{
    private readonly NativeVocoderApi api;

    public SoftwareVocoderBackend(string? libraryPath = null)
    {
        api = NativeVocoderApi.Load(libraryPath);
    }

    public string Name => "dvmvocoder";

    public bool IsAvailable => api.IsLoaded;

    public IVocoderSession CreateSession(VocoderMode mode)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("The software vocoder library is not loaded.");

        return new SoftwareVocoderSession(api, mode);
    }

    public void Dispose() => api.Dispose();

    private sealed class SoftwareVocoderSession : IVocoderSession
    {
        private readonly NativeVocoderApi api;
        private readonly VocoderMode mode;
        private IntPtr encoderHandle;
        private IntPtr decoderHandle;

        public SoftwareVocoderSession(NativeVocoderApi api, VocoderMode mode)
        {
            this.api = api;
            this.mode = mode;
            encoderHandle = api.CreateEncoder((int)mode);
            decoderHandle = api.CreateDecoder((int)mode);
            if (encoderHandle == IntPtr.Zero || decoderHandle == IntPtr.Zero)
            {
                if (encoderHandle != IntPtr.Zero)
                    api.DeleteEncoder(encoderHandle);
                if (decoderHandle != IntPtr.Zero)
                    api.DeleteDecoder(decoderHandle);

                throw new InvalidOperationException($"Unable to create a {mode} vocoder session.");
            }
        }

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            ValidateFrame(samples.Length, codeword.Length);
            short[] sampleArray = samples.ToArray();
            byte[] codewordArray = new byte[codeword.Length];
            api.Encode(encoderHandle, sampleArray, codewordArray);
            codewordArray.AsSpan().CopyTo(codeword);
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            ValidateFrame(samples.Length, codeword.Length);
            byte[] codewordArray = codeword.ToArray();
            short[] sampleArray = new short[samples.Length];
            int errors = api.Decode(decoderHandle, codewordArray, sampleArray);
            sampleArray.AsSpan().CopyTo(samples);
            return errors;
        }

        private void ValidateFrame(int sampleCount, int codewordLength)
        {
            if (sampleCount != VocoderFrameSizes.PcmSamplesPerFrame)
                throw new ArgumentException($"A vocoder frame requires {VocoderFrameSizes.PcmSamplesPerFrame} PCM samples.", nameof(sampleCount));

            int expectedCodewordLength = VocoderFrameSizes.CodewordBytes(mode);
            if (codewordLength != expectedCodewordLength)
                throw new ArgumentException($"A {mode} vocoder codeword must be {expectedCodewordLength} bytes.", nameof(codewordLength));
        }

        public void Dispose()
        {
            if (encoderHandle == IntPtr.Zero && decoderHandle == IntPtr.Zero)
                return;

            if (encoderHandle != IntPtr.Zero)
            {
                api.DeleteEncoder(encoderHandle);
                encoderHandle = IntPtr.Zero;
            }

            if (decoderHandle != IntPtr.Zero)
            {
                api.DeleteDecoder(decoderHandle);
                decoderHandle = IntPtr.Zero;
            }
        }
    }

    private sealed class NativeVocoderApi : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CreateDelegate(int mode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EncodeDelegate(IntPtr encoder, short[] samples, byte[] codeword);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DeleteDelegate(IntPtr encoder);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CreateDecoderDelegate(int mode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DecodeDelegate(IntPtr decoder, byte[] codeword, short[] samples);

        private readonly IntPtr libraryHandle;
        private readonly CreateDelegate createEncoder;
        private readonly EncodeDelegate encode;
        private readonly DeleteDelegate deleteEncoder;
        private readonly CreateDecoderDelegate createDecoder;
        private readonly DecodeDelegate decode;
        private readonly DeleteDelegate deleteDecoder;

        private NativeVocoderApi(
            IntPtr libraryHandle,
            CreateDelegate createEncoder,
            EncodeDelegate encode,
            DeleteDelegate deleteEncoder,
            CreateDecoderDelegate createDecoder,
            DecodeDelegate decode,
            DeleteDelegate deleteDecoder)
        {
            this.libraryHandle = libraryHandle;
            this.createEncoder = createEncoder;
            this.encode = encode;
            this.deleteEncoder = deleteEncoder;
            this.createDecoder = createDecoder;
            this.decode = decode;
            this.deleteDecoder = deleteDecoder;
            IsLoaded = true;
        }

        public bool IsLoaded { get; }

        public static NativeVocoderApi Load(string? libraryPath)
        {
            string resolvedPath = ResolveLibraryPath(libraryPath);
            IntPtr handle = NativeLibrary.Load(resolvedPath);

            try
            {
                return new NativeVocoderApi(
                    handle,
                    GetDelegate<CreateDelegate>(handle, "MBEEncoder_Create"),
                    GetDelegate<EncodeDelegate>(handle, "MBEEncoder_Encode"),
                    GetDelegate<DeleteDelegate>(handle, "MBEEncoder_Delete"),
                    GetDelegate<CreateDecoderDelegate>(handle, "MBEDecoder_Create"),
                    GetDelegate<DecodeDelegate>(handle, "MBEDecoder_Decode"),
                    GetDelegate<DeleteDelegate>(handle, "MBEDecoder_Delete"));
            }
            catch
            {
                NativeLibrary.Free(handle);
                throw;
            }
        }

        public IntPtr CreateEncoder(int mode) => createEncoder(mode);

        public void Encode(IntPtr encoder, short[] samples, byte[] codeword) => encode(encoder, samples, codeword);

        public void DeleteEncoder(IntPtr encoder) => deleteEncoder(encoder);

        public IntPtr CreateDecoder(int mode) => createDecoder(mode);

        public int Decode(IntPtr decoder, byte[] codeword, short[] samples) => decode(decoder, codeword, samples);

        public void DeleteDecoder(IntPtr decoder) => deleteDecoder(decoder);

        public void Dispose() => NativeLibrary.Free(libraryHandle);

        private static TDelegate GetDelegate<TDelegate>(IntPtr handle, string symbol)
            where TDelegate : Delegate
        {
            IntPtr address = NativeLibrary.GetExport(handle, symbol);
            return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        }

        private static string ResolveLibraryPath(string? libraryPath)
        {
            if (!string.IsNullOrWhiteSpace(libraryPath))
                return Path.GetFullPath(libraryPath);

            string[] names = OperatingSystem.IsMacOS()
                ? ["libvocoder.dylib", "libvocoder"]
                : OperatingSystem.IsWindows()
                    ? ["libvocoder.dll", "libvocoder"]
                    : ["libvocoder.so", "libvocoder"];

            foreach (string name in names)
            {
                string candidate = Path.Combine(AppContext.BaseDirectory, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            return names[0];
        }
    }
}
