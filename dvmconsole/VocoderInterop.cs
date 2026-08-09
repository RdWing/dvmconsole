// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2025 Patrick McDonnell, W3AXL
*
*/

using System.Diagnostics;
using System.Runtime.InteropServices;
using fnecore;

namespace dvmconsole
{
    /// <summary>
    ///
    /// </summary>
    public enum MBE_MODE
    {
        DMR_AMBE,    //! DMR AMBE
        IMBE_88BIT,  //! 88-bit IMBE (P25)
    } // public enum MBE_MODE

    /// <summary>
    /// Shared frame-size and mode helpers for the native codec wrappers.
    /// The native C ABI carries no buffer lengths, so the managed wrappers
    /// enforce the documented frame sizes before every native call.
    /// </summary>
    internal static class MBECodec
    {
        public static void ValidateMode(MBE_MODE mode)
        {
            if (mode != MBE_MODE.DMR_AMBE && mode != MBE_MODE.IMBE_88BIT)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, $"Unsupported MBE_MODE value: {(int)mode}");
        }

        public static int CodeBytesFor(MBE_MODE mode) =>
            mode == MBE_MODE.DMR_AMBE ? MBEInterleaver.AMBE_CODEWORD_SAMPLES : MBEInterleaver.IMBE_CODEWORD_SAMPLES;

        public static int CodeBitsFor(MBE_MODE mode) =>
            mode == MBE_MODE.DMR_AMBE ? MBEInterleaver.AMBE_CODEWORD_BITS : MBEInterleaver.IMBE_CODEWORD_BITS;
    } // internal static class MBECodec

    /// <summary>
    /// Wrapper class for the C++ dvmvocoder encoder library.
    /// </summary>
    /// Using info from https://stackoverflow.com/a/315064/1842613
    public class MBEEncoder : IDisposable
    {
        private readonly object gate = new object();
        private readonly MBE_MODE mode;
        private IntPtr encoder;

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="MBEEncoder"/> class.
        /// </summary>
        /// <param name="mode">Vocoder Mode (DMR or P25)</param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        public MBEEncoder(MBE_MODE mode)
        {
            MBECodec.ValidateMode(mode);
            this.mode = mode;
            encoder = MBEEncoder_Create(mode);
            if (encoder == IntPtr.Zero)
                throw new InvalidOperationException("MBEEncoder_Create returned IntPtr.Zero! The native libvocoder encoder could not be created.");
        }

        /// <summary>
        /// Releases the native encoder. Idempotent and thread-safe: concurrent
        /// calls (and the finalizer path) serialize on an internal gate, so the
        /// native handle is deleted exactly once and never while an encode is
        /// in flight.
        /// </summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (encoder != IntPtr.Zero)
                {
                    MBEEncoder_Delete(encoder);
                    encoder = IntPtr.Zero;
                }
            }
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizes a instance of the <see cref="MBEEncoder"/> class.
        /// </summary>
        ~MBEEncoder()
        {
            Dispose();
        }

        /// <summary>
        /// Create a new MBEEncoder
        /// </summary>
        /// <returns></returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MBEEncoder_Create(MBE_MODE mode);

        /// <summary>
        /// Encode PCM16 samples to MBE codeword
        /// </summary>
        /// <param name="pEncoder">Native encoder handle</param>
        /// <param name="samples">Input PCM samples</param>
        /// <param name="codeword">Output MBE codeword</param>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern void MBEEncoder_Encode(IntPtr pEncoder, [In] Int16[] samples, [Out] byte[] codeword);

        /// <summary>
        /// Encode MBE to bits
        /// </summary>
        /// <param name="pEncoder"></param>
        /// <param name="bits"></param>
        /// <param name="codeword"></param>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern void MBEEncoder_EncodeBits(IntPtr pEncoder, [In] byte[] bits, [Out] byte[] codeword);

        /// <summary>
        /// Delete a created MBEEncoder
        /// </summary>
        /// <param name="pEncoder"></param>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern void MBEEncoder_Delete(IntPtr pEncoder);

        /// <summary>
        /// Encode PCM16 samples to MBE codeword
        /// </summary>
        /// <param name="samples">Input PCM samples (must contain exactly 160 samples)</param>
        /// <param name="codeword">Output MBE codeword (must be exactly 9 bytes DMR / 11 bytes IMBE)</param>
        /// <exception cref="ObjectDisposedException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public void encode([In] Int16[] samples, [Out] byte[] codeword)
        {
            lock (gate)
            {
                if (encoder == IntPtr.Zero)
                    throw new ObjectDisposedException(nameof(MBEEncoder));
                if (samples == null)
                    throw new ArgumentNullException(nameof(samples));
                if (codeword == null)
                    throw new ArgumentNullException(nameof(codeword));
                if (samples.Length != MBEInterleaver.PCM_SAMPLES)
                    throw new ArgumentOutOfRangeException(nameof(samples), $"PCM sample array must contain exactly {MBEInterleaver.PCM_SAMPLES} samples, was {samples.Length}.");
                if (codeword.Length != MBECodec.CodeBytesFor(mode))
                    throw new ArgumentOutOfRangeException(nameof(codeword), $"Codeword array must be {MBECodec.CodeBytesFor(mode)} bytes for {mode}, was {codeword.Length}.");

                MBEEncoder_Encode(encoder, samples, codeword);
            }
            GC.KeepAlive(this);
        }

        /// <summary>
        /// Encode MBE bits to a codeword
        /// </summary>
        /// <param name="bits">Input MBE bits (must contain exactly 49 bits DMR / 88 bits IMBE)</param>
        /// <param name="codeword">Output MBE codeword (must be exactly 9 bytes DMR / 11 bytes IMBE)</param>
        /// <exception cref="ObjectDisposedException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public void encodeBits([In] byte[] bits, [Out] byte[] codeword)
        {
            lock (gate)
            {
                if (encoder == IntPtr.Zero)
                    throw new ObjectDisposedException(nameof(MBEEncoder));
                if (bits == null)
                    throw new ArgumentNullException(nameof(bits));
                if (codeword == null)
                    throw new ArgumentNullException(nameof(codeword));
                if (bits.Length != MBECodec.CodeBitsFor(mode))
                    throw new ArgumentOutOfRangeException(nameof(bits), $"Bit array must contain {MBECodec.CodeBitsFor(mode)} bits for {mode}, was {bits.Length}.");
                if (codeword.Length != MBECodec.CodeBytesFor(mode))
                    throw new ArgumentOutOfRangeException(nameof(codeword), $"Codeword array must be {MBECodec.CodeBytesFor(mode)} bytes for {mode}, was {codeword.Length}.");

                MBEEncoder_EncodeBits(encoder, bits, codeword);
            }
            GC.KeepAlive(this);
        }
    } // public class MBEEncoder

    /// <summary>
    /// Wrapper class for the C++ dvmvocoder decoder library.
    /// </summary>
    public class MBEDecoder : IDisposable
    {
        private readonly object gate = new object();
        private readonly MBE_MODE mode;
        private IntPtr decoder;

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="MBEDecoder"/> class.
        /// </summary>
        /// <param name="mode">Vocoder Mode (DMR or P25)</param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        public MBEDecoder(MBE_MODE mode)
        {
            MBECodec.ValidateMode(mode);
            this.mode = mode;
            decoder = MBEDecoder_Create(mode);
            if (decoder == IntPtr.Zero)
                throw new InvalidOperationException("MBEDecoder_Create returned IntPtr.Zero! The native libvocoder decoder could not be created.");
        }

        /// <summary>
        /// Releases the native decoder. Idempotent and thread-safe: concurrent
        /// calls (and the finalizer path) serialize on an internal gate, so the
        /// native handle is deleted exactly once and never while a decode is
        /// in flight.
        /// </summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (decoder != IntPtr.Zero)
                {
                    MBEDecoder_Delete(decoder);
                    decoder = IntPtr.Zero;
                }
            }
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizes a instance of the <see cref="MBEDecoder"/> class.
        /// </summary>
        ~MBEDecoder()
        {
            Dispose();
        }

        /// <summary>
        /// Create a new MBEDecoder
        /// </summary>
        /// <returns></returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MBEDecoder_Create(MBE_MODE mode);

        /// <summary>
        /// Decode MBE codeword to samples
        /// </summary>
        /// <param name="pDecoder">Native decoder handle</param>
        /// <param name="codeword">Input MBE codeword</param>
        /// <param name="samples">Output PCM samples</param>
        /// <returns>Number of decode errors</returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int32 MBEDecoder_Decode(IntPtr pDecoder, [In] byte[] codeword, [Out] Int16[] samples);

        /// <summary>
        /// Decode MBE to bits
        /// </summary>
        /// <param name="pDecoder"></param>
        /// <param name="codeword"></param>
        /// <param name="mbeBits"></param>
        /// <returns></returns>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern Int32 MBEDecoder_DecodeBits(IntPtr pDecoder, [In] byte[] codeword, [Out] byte[] bits);

        /// <summary>
        /// Delete a created MBEDecoder
        /// </summary>
        /// <param name="pDecoder"></param>
        [DllImport("libvocoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern void MBEDecoder_Delete(IntPtr pDecoder);

        /// <summary>
        /// Decode MBE codeword to PCM16 samples
        /// </summary>
        /// <param name="codeword">Input MBE codeword (must be exactly 9 bytes DMR / 11 bytes IMBE)</param>
        /// <param name="samples">Output PCM samples (must contain exactly 160 samples)</param>
        /// <exception cref="ObjectDisposedException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public Int32 decode([In] byte[] codeword, [Out] Int16[] samples)
        {
            Int32 ret;
            lock (gate)
            {
                if (decoder == IntPtr.Zero)
                    throw new ObjectDisposedException(nameof(MBEDecoder));
                if (codeword == null)
                    throw new ArgumentNullException(nameof(codeword));
                if (samples == null)
                    throw new ArgumentNullException(nameof(samples));
                if (codeword.Length != MBECodec.CodeBytesFor(mode))
                    throw new ArgumentOutOfRangeException(nameof(codeword), $"Codeword array must be {MBECodec.CodeBytesFor(mode)} bytes for {mode}, was {codeword.Length}.");
                if (samples.Length != MBEInterleaver.PCM_SAMPLES)
                    throw new ArgumentOutOfRangeException(nameof(samples), $"PCM sample array must contain exactly {MBEInterleaver.PCM_SAMPLES} samples, was {samples.Length}.");

                ret = MBEDecoder_Decode(decoder, codeword, samples);
            }
            GC.KeepAlive(this);
            return ret;
        }

        /// <summary>
        /// Decode MBE codeword to bits
        /// </summary>
        /// <param name="codeword">Input MBE codeword (must be exactly 9 bytes DMR / 11 bytes IMBE)</param>
        /// <param name="bits">Output MBE bits (must contain exactly 49 bits DMR / 88 bits IMBE)</param>
        /// <exception cref="ObjectDisposedException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public Int32 decodeBits([In] byte[] codeword, [Out] byte[] bits)
        {
            Int32 ret;
            lock (gate)
            {
                if (decoder == IntPtr.Zero)
                    throw new ObjectDisposedException(nameof(MBEDecoder));
                if (codeword == null)
                    throw new ArgumentNullException(nameof(codeword));
                if (bits == null)
                    throw new ArgumentNullException(nameof(bits));
                if (codeword.Length != MBECodec.CodeBytesFor(mode))
                    throw new ArgumentOutOfRangeException(nameof(codeword), $"Codeword array must be {MBECodec.CodeBytesFor(mode)} bytes for {mode}, was {codeword.Length}.");
                if (bits.Length != MBECodec.CodeBitsFor(mode))
                    throw new ArgumentOutOfRangeException(nameof(bits), $"Bit array must contain {MBECodec.CodeBitsFor(mode)} bits for {mode}, was {bits.Length}.");

                ret = MBEDecoder_DecodeBits(decoder, codeword, bits);
            }
            GC.KeepAlive(this);
            return ret;
        }
    } // public class MBEDecoder

    /// <summary>
    ///
    /// </summary>
    public static class MBEToneGenerator
    {
        /// <summary>
        /// Encodes a single tone to an AMBE tone frame
        /// </summary>
        /// <param name="tone_freq_hz"></param>
        /// <param name="tone_amplitude"></param>
        /// <param name="codeword"></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public static void AMBEEncodeSingleTone(int tone_freq_hz, char tone_amplitude, [Out] byte[] codeword)
        {
            // U bit vectors
            // u0 and u1 are 12 bits
            // u2 is 11 bits
            // u3 is 14 bits
            // total length is 49 bits
            ushort[] u = new ushort[4];

            // Convert the tone frequency to the nearest tone index
            uint tone_idx = (uint)((float)tone_freq_hz / 31.25f);

            // Validate tone index
            if (tone_idx < 5 || tone_idx > 122)
                throw new ArgumentOutOfRangeException($"Tone index for frequency out of range!");

            // Validate amplitude value
            if (tone_amplitude > 127)
                throw new ArgumentOutOfRangeException("Tone amplitude must be between 0 and 127!");

            // Make sure tone index only has 7 bits (it should but we make sure :) )
            tone_idx &= 0b01111111;

            // Encode u vectors per TIA-102.BABA-1 section 7.2

            // u0[11-6] are always 1 to indicate a tone, so we left-shift 63u (0x00111111) a full byte (8 bits)
            u[0] |= (ushort)(63 << 8);

            // u0[5-0] are AD (tone amplitude byte) bits 6-1
            u[0] |= (ushort)(tone_amplitude >> 1);

            // u1[11-4] are tone index bits 7-0 (the full byte)
            u[1] |= (ushort)(tone_idx << 4);

            // u1[3-0] are tone index bits 7-4
            u[1] |= (ushort)(tone_idx >> 4);

            // u2[10-7] are tone index bits 3-0
            u[2] |= (ushort)((tone_idx & 0b00001111) << 7);

            // u2[6-0] are tone index bits 7-1
            u[2] |= (ushort)(tone_idx >> 1);

            // u3[13] is the last bit of the tone index
            u[3] |= (ushort)((tone_idx & 0b1) << 13);

            // u3[12-5] is the full tone index byte
            u[3] |= (ushort)(tone_idx << 5);

            // u3[4] is the last bit of the amplitude byte
            u[3] |= (ushort)((tone_amplitude & 0b1) << 4);

            // u3[3-0] is always 0 so we don't have to do anything here

            // Convert u buffer to byte
            Buffer.BlockCopy(u, 0, codeword, 0, 8);
        }

        /// <summary>
        /// Encode a single tone to an IMBE codeword sequence using a lookup table
        /// </summary>
        /// <param name="tone_freq_hz"></param>
        /// <param name="codeword"></param>
        public static void IMBEEncodeSingleTone(ushort tone_freq_hz, [Out] byte[] codeword)
        {
            // Find nearest tone in the lookup table
            List<ushort> tone_keys = VocoderToneLookupTable.IMBEToneFrames.Keys.ToList();
            ushort nearest = tone_keys.Aggregate((x, y) => Math.Abs(x - tone_freq_hz) < Math.Abs(y - tone_freq_hz) ? x : y);
            byte[] tone_codeword = VocoderToneLookupTable.IMBEToneFrames[nearest];
            Array.Copy(tone_codeword, codeword, tone_codeword.Length);
        }
    } // public static class MBEToneGenerator

    /// <summary>
    ///
    /// </summary>
    public class MBEInterleaver : IDisposable
    {
        public const int PCM_SAMPLES = 160;
        public const int AMBE_CODEWORD_SAMPLES = 9;
        public const int AMBE_CODEWORD_BITS = 49;
        public const int IMBE_CODEWORD_SAMPLES = 11;
        public const int IMBE_CODEWORD_BITS = 88;

        private readonly object gate = new object();

        private MBE_MODE mode;

        private MBEEncoder encoder;
        private MBEDecoder decoder;

        private bool disposed;

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="MBEInterleaver"/> class.
        /// </summary>
        /// <param name="mode"></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public MBEInterleaver(MBE_MODE mode)
        {
            MBECodec.ValidateMode(mode);
            this.mode = mode;
            encoder = new MBEEncoder(this.mode);
            try
            {
                decoder = new MBEDecoder(this.mode);
            }
            catch
            {
                // Do not leak the already-created native encoder if decoder
                // creation fails.
                encoder.Dispose();
                encoder = null;
                throw;
            }
        }

        /// <summary>
        /// Releases the owned encoder and decoder (deterministic, idempotent,
        /// thread-safe: concurrent calls and the finalizer path serialize on an
        /// internal gate, so the children are disposed exactly once and never
        /// while an Encode/Decode is in flight).
        /// </summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (!disposed)
                {
                    disposed = true;
                    encoder.Dispose();
                    decoder.Dispose();
                }
            }
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="codeword"></param>
        /// <param name="mbeBits"></param>
        /// <returns></returns>
        /// <exception cref="NullReferenceException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public int Decode([In] byte[] codeword, [Out] byte[] mbeBits)
        {
            int errs;
            lock (gate)
            {
                // Input validation
                if (disposed)
                    throw new ObjectDisposedException(nameof(MBEInterleaver));
                if (codeword == null)
                    throw new ArgumentNullException(nameof(codeword));
                if (mbeBits == null)
                    throw new ArgumentNullException(nameof(mbeBits));
                if (mbeBits.Length != MBECodec.CodeBitsFor(mode))
                    throw new ArgumentOutOfRangeException(nameof(mbeBits), $"Bit array must contain {MBECodec.CodeBitsFor(mode)} bits for {mode}, was {mbeBits.Length}.");

                byte[] bits = null;
                int bitCount = 0;

                // Set up based on mode
                if (mode == MBE_MODE.DMR_AMBE)
                {
                    if (codeword.Length != AMBE_CODEWORD_SAMPLES)
                        throw new ArgumentOutOfRangeException($"AMBE codeword length is != {AMBE_CODEWORD_SAMPLES}");
                    bitCount = AMBE_CODEWORD_BITS;
                    bits = new byte[bitCount];
                }
                else if (mode == MBE_MODE.IMBE_88BIT)
                {
                    if (codeword.Length != IMBE_CODEWORD_SAMPLES)
                        throw new ArgumentOutOfRangeException($"IMBE codeword length is != {IMBE_CODEWORD_SAMPLES}");
                    bitCount = IMBE_CODEWORD_BITS;
                    bits = new byte[bitCount];
                }

                if (bits == null)
                    throw new NullReferenceException("Failed to initialize decoder");

                // Decode
                errs = decoder.decodeBits(codeword, bits);

                // Copy
                for (int i = 0; i < bitCount; i++)
                    mbeBits[i] = (byte)(bits[i] & 0x01);
            }
            GC.KeepAlive(this);
            return errs;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="mbeBits"></param>
        /// <param name="codeword"></param>
        /// <exception cref="NullReferenceException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        /// <exception cref="ArgumentException"></exception>
        public void Encode([In] byte[] mbeBits, [Out] byte[] codeword)
        {
            lock (gate)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(MBEInterleaver));
                if (mbeBits == null)
                {
                    throw new ArgumentNullException(nameof(mbeBits));
                }
                if (codeword == null)
                {
                    throw new ArgumentNullException(nameof(codeword));
                }
                if (codeword.Length != MBECodec.CodeBytesFor(mode))
                    throw new ArgumentOutOfRangeException(nameof(codeword), $"Codeword array must be {MBECodec.CodeBytesFor(mode)} bytes for {mode}, was {codeword.Length}.");

                byte[] bits = null;

                // Set up based on mode
                if (mode == MBE_MODE.DMR_AMBE)
                {
                    if (mbeBits.Length != AMBE_CODEWORD_BITS)
                    {
                        throw new ArgumentOutOfRangeException($"AMBE codeword bit length is != {AMBE_CODEWORD_BITS}");
                    }
                    bits = new byte[AMBE_CODEWORD_BITS];
                    for (int i = 0; i < mbeBits.Length; i++)
                        bits[i] = (byte)(mbeBits[i] & 0x01);
                }
                else if (mode == MBE_MODE.IMBE_88BIT)
                {
                    if (mbeBits.Length != IMBE_CODEWORD_BITS)
                    {
                        throw new ArgumentOutOfRangeException($"IMBE codeword bit length is != {IMBE_CODEWORD_BITS}");
                    }
                    bits = new byte[IMBE_CODEWORD_BITS];
                    for (int i = 0; i < mbeBits.Length; i++)
                        bits[i] = (byte)(mbeBits[i] & 0x01);
                }

                if (bits == null)
                {
                    throw new ArgumentException("Bit array did not get set up properly!");
                }

                // Encode samples
                if (mode == MBE_MODE.DMR_AMBE)
                {
                    // Create output array
                    byte[] codewords = new byte[AMBE_CODEWORD_SAMPLES];
                    // Encode
                    encoder.encodeBits(bits, codewords);
                    // Copy
                    for (int i = 0; i < AMBE_CODEWORD_SAMPLES; i++)
                        codeword[i] = codewords[i];
                }
                else if (mode == MBE_MODE.IMBE_88BIT)
                {
                    // Create output array
                    byte[] codewords = new byte[IMBE_CODEWORD_SAMPLES];
                    // Encode
                    encoder.encodeBits(bits, codewords);
                    // Copy
                    for (int i = 0; i < IMBE_CODEWORD_SAMPLES; i++)
                        codeword[i] = codewords[i];
                }
            }
            GC.KeepAlive(this);
        }
    } // public class MBEInterleaver
} // namespace dvmconsole
