// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio.Vocoder
{
    /// <summary>
    /// DUAL-MODE voice codec adapter implementing
    /// <see cref="IVoiceFrameDecoder"/> and <see cref="IVoiceFrameEncoder"/>
    /// over the 8-export libvocoder C ABI behind an injectable
    /// <see cref="ILibVocoderNative"/> seam. Owns four native handles
    /// created in the constructor — DMR encoder, DMR decoder, P25
    /// encoder, P25 decoder — and dispatches decode by codeword length
    /// (9 bytes -&gt; DMR handle, 11 bytes -&gt; P25 handle), keeping the
    /// mode-agnostic <see cref="TalkgroupAudioRouter"/> wiring intact;
    /// encode is mode-aware because the native encoder handle is
    /// mode-bound while the PCM input is not.
    ///
    /// Decode errs semantics (WPF parity MainWindow.DMR.cs:197-217): a
    /// decoded frame is SUCCESS whenever its shape is valid and the
    /// native handle is alive — <see cref="TryDecode(ReadOnlyMemory{byte}, out short[])"/>
    /// returns false ONLY for the native -1 NULL-handle sentinel
    /// (<see cref="NativeDecodeFailed"/>) and for shape violations. The
    /// native error count is informational and there is NO threshold:
    /// WPF plays every frame and only logs errs, so a future edit adding
    /// a cutoff would silently drop voice. Encode has no failure return
    /// at all natively, so <see cref="TryEncode(VoiceMode, ReadOnlyMemory{short}, out byte[])"/>
    /// fails only via shape/handle guards.
    ///
    /// Lifecycle (WPF MBEInterleaver parity): all operations and
    /// <see cref="Dispose"/> serialize on a single gate lock; Dispose
    /// deletes each of the four handles exactly once and is idempotent;
    /// use after Dispose throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    public sealed class LibVocoderVoiceCodec : IVoiceFrameDecoder, IVoiceFrameEncoder, IDisposable
    {
        /*
        ** Constants (WPF MBEInterleaver parity, VocoderInterop.cs:434-438)
        */

        /// <summary>PCM samples per 20 ms voice frame at 8000 Hz, 16-bit, mono.</summary>
        public const int PcmSamples = 160;

        /// <summary>DMR AMBE codeword length in bytes.</summary>
        public const int AmbeCodeBytes = 9;

        /// <summary>P25 IMBE codeword length in bytes.</summary>
        public const int ImbeCodeBytes = 11;

        /// <summary>Native decode sentinel for a NULL/invalid handle; the
        /// only native result that means failure.</summary>
        public const int NativeDecodeFailed = -1;

        /*
        ** Fields
        */

        private readonly object gate = new();
        private readonly ILibVocoderNative native;

        private IntPtr dmrEncoder;
        private IntPtr dmrDecoder;
        private IntPtr p25Encoder;
        private IntPtr p25Decoder;
        private bool disposed;

        /*
        ** Constructors
        */

        /// <summary>
        /// Creates the codec adapter and its four native handles, in the
        /// fixed order DMR encoder, DMR decoder, P25 encoder, P25 decoder.
        /// When any Create returns <see cref="IntPtr.Zero"/> the
        /// constructor throws <see cref="InvalidOperationException"/> and
        /// deletes every handle created before the failure, so no native
        /// handle can leak (WPF MBEInterleaver parity, VocoderInterop.cs:462-474).
        /// </summary>
        /// <param name="native">The native seam supplying the 8 C-ABI calls.</param>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="native"/> is null.</exception>
        /// <exception cref="InvalidOperationException">When any native
        /// Create returns <see cref="IntPtr.Zero"/>; already-created
        /// handles are deleted before the exception escapes.</exception>
        public LibVocoderVoiceCodec(ILibVocoderNative native)
        {
            this.native = native ?? throw new ArgumentNullException(nameof(native));

            dmrEncoder = this.native.MBEEncoder_Create(VocoderMode.Dmr);
            if (dmrEncoder == IntPtr.Zero)
            {
                throw CreateFailed(VocoderMode.Dmr, encoder: true);
            }

            dmrDecoder = this.native.MBEDecoder_Create(VocoderMode.Dmr);
            if (dmrDecoder == IntPtr.Zero)
            {
                RollbackCreated();
                throw CreateFailed(VocoderMode.Dmr, encoder: false);
            }

            p25Encoder = this.native.MBEEncoder_Create(VocoderMode.P25);
            if (p25Encoder == IntPtr.Zero)
            {
                RollbackCreated();
                throw CreateFailed(VocoderMode.P25, encoder: true);
            }

            p25Decoder = this.native.MBEDecoder_Create(VocoderMode.P25);
            if (p25Decoder == IntPtr.Zero)
            {
                RollbackCreated();
                throw CreateFailed(VocoderMode.P25, encoder: false);
            }
        }

        /*
        ** Decode
        */

        /// <summary>
        /// Decodes one voice codeword into 160 16-bit PCM samples. The
        /// mode is dispatched by codeword length: 9 bytes routes to the
        /// DMR decoder handle, 11 bytes to the P25 decoder handle; any
        /// other length throws before the native call is ever invoked.
        /// The codeword is copied to an exact-length array before the
        /// native call, so a partially accepted buffer is impossible.
        ///
        /// Success is shape-valid plus handle-alive: the native error
        /// count is informational with NO threshold (WPF parity
        /// MainWindow.DMR.cs:197-217 plays every frame and only logs
        /// errs), so any native result other than the -1 NULL-handle
        /// sentinel (<see cref="NativeDecodeFailed"/>) is success.
        /// </summary>
        /// <param name="voiceFrame">The encoded voice codeword: 9 bytes
        /// for DMR, 11 bytes for P25.</param>
        /// <param name="samples">The decoded 160 16-bit PCM samples on
        /// success; empty on failure.</param>
        /// <returns>True when the codeword decoded into
        /// <paramref name="samples"/>.</returns>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="voiceFrame"/> is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">When
        /// <paramref name="voiceFrame"/> is not exactly 9 or 11 bytes.</exception>
        /// <exception cref="ObjectDisposedException">When the codec has
        /// been disposed.</exception>
        public bool TryDecode(ReadOnlyMemory<byte> voiceFrame, out short[] samples)
        {
            lock (gate)
            {
                ThrowIfDisposed();

                if (voiceFrame.IsEmpty)
                {
                    throw new ArgumentNullException(nameof(voiceFrame));
                }

                IntPtr handle;
                switch (voiceFrame.Length)
                {
                    case AmbeCodeBytes:
                        handle = dmrDecoder;
                        break;
                    case ImbeCodeBytes:
                        handle = p25Decoder;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(voiceFrame),
                            voiceFrame.Length,
                            $"A voice codeword must be exactly {AmbeCodeBytes} bytes (DMR) or {ImbeCodeBytes} bytes (P25), was {voiceFrame.Length}.");
                }

                // Exact-length copy before the native call: the C ABI
                // carries no buffer length, so the native code must never
                // observe a partially accepted buffer.
                var codeword = voiceFrame.ToArray();
                var buffer = new short[PcmSamples];
                var errs = native.MBEDecoder_Decode(handle, codeword, buffer);

                if (errs == NativeDecodeFailed)
                {
                    samples = Array.Empty<short>();
                    return false;
                }

                samples = buffer;
                return true;
            }
        }

        /*
        ** Encode
        */

        /// <summary>
        /// Encodes one 20 ms PCM voice frame into one voice codeword. The
        /// mode is caller-supplied because the input is mode-agnostic
        /// (always 160 samples) while the native encoder handle is
        /// mode-bound: <see cref="VoiceMode.Dmr"/> selects the DMR
        /// encoder handle and produces a 9-byte AMBE codeword,
        /// <see cref="VoiceMode.P25"/> the P25 encoder handle and an
        /// 11-byte IMBE codeword. Encode has no failure return natively,
        /// so this method fails only via the shape and handle guards.
        /// </summary>
        /// <param name="mode">The voice mode selecting the encoder handle.</param>
        /// <param name="samples">The 160 16-bit PCM samples to encode.</param>
        /// <param name="codeword">The encoded codeword: 9 bytes for DMR,
        /// 11 bytes for P25.</param>
        /// <returns>True when the samples encoded into
        /// <paramref name="codeword"/>.</returns>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="samples"/> is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">When
        /// <paramref name="samples"/> is not exactly
        /// <see cref="PcmSamples"/> samples, or when
        /// <paramref name="mode"/> is unsupported.</exception>
        /// <exception cref="ObjectDisposedException">When the codec has
        /// been disposed.</exception>
        public bool TryEncode(VoiceMode mode, ReadOnlyMemory<short> samples, out byte[] codeword)
        {
            lock (gate)
            {
                ThrowIfDisposed();

                if (samples.IsEmpty)
                {
                    throw new ArgumentNullException(nameof(samples));
                }

                if (samples.Length != PcmSamples)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(samples),
                        samples.Length,
                        $"A PCM voice frame must contain exactly {PcmSamples} samples, was {samples.Length}.");
                }

                IntPtr handle;
                int length;
                switch (mode)
                {
                    case VoiceMode.Dmr:
                        handle = dmrEncoder;
                        length = AmbeCodeBytes;
                        break;
                    case VoiceMode.P25:
                        handle = p25Encoder;
                        length = ImbeCodeBytes;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(mode),
                            mode,
                            $"Unsupported voice mode: {(int)mode}");
                }

                // Exact-length copy before the native call: the C ABI
                // carries no buffer length, so the native code must never
                // observe a partially accepted buffer.
                var buffer = new byte[length];
                native.MBEEncoder_Encode(handle, samples.ToArray(), buffer);
                codeword = buffer;
                return true;
            }
        }

        /*
        ** Lifecycle
        */

        /// <summary>
        /// Deletes each of the four native handles exactly once.
        /// Idempotent and thread-safe: all operations and disposal
        /// serialize on a single gate (WPF parity), so a concurrent
        /// Dispose can never double-delete a handle and never runs while
        /// a decode/encode is in flight.
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

                if (dmrEncoder != IntPtr.Zero)
                {
                    native.MBEEncoder_Delete(dmrEncoder);
                    dmrEncoder = IntPtr.Zero;
                }

                if (dmrDecoder != IntPtr.Zero)
                {
                    native.MBEDecoder_Delete(dmrDecoder);
                    dmrDecoder = IntPtr.Zero;
                }

                if (p25Encoder != IntPtr.Zero)
                {
                    native.MBEEncoder_Delete(p25Encoder);
                    p25Encoder = IntPtr.Zero;
                }

                if (p25Decoder != IntPtr.Zero)
                {
                    native.MBEDecoder_Delete(p25Decoder);
                    p25Decoder = IntPtr.Zero;
                }
            }
        }

        /*
        ** Helpers
        */

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(LibVocoderVoiceCodec));
            }
        }

        /// <summary>
        /// Deletes every handle created before a later Create failed, in
        /// creation order, so the constructor never leaks native handles
        /// (WPF MBEInterleaver parity, VocoderInterop.cs:462-474).
        /// </summary>
        private void RollbackCreated()
        {
            if (dmrEncoder != IntPtr.Zero)
            {
                native.MBEEncoder_Delete(dmrEncoder);
                dmrEncoder = IntPtr.Zero;
            }

            if (dmrDecoder != IntPtr.Zero)
            {
                native.MBEDecoder_Delete(dmrDecoder);
                dmrDecoder = IntPtr.Zero;
            }

            if (p25Encoder != IntPtr.Zero)
            {
                native.MBEEncoder_Delete(p25Encoder);
                p25Encoder = IntPtr.Zero;
            }

            if (p25Decoder != IntPtr.Zero)
            {
                native.MBEDecoder_Delete(p25Decoder);
                p25Decoder = IntPtr.Zero;
            }
        }

        private static InvalidOperationException CreateFailed(VocoderMode mode, bool encoder)
            => new(
                $"MBE{(encoder ? "Encoder" : "Decoder")}_Create returned IntPtr.Zero! "
                + $"The native libvocoder {(encoder ? "encoder" : "decoder")} could not be created for {mode}.");
    }
}
