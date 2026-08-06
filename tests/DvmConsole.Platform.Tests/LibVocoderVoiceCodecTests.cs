// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the libvocoder P/Invoke adapter slice:
*
*   DvmConsole.Platform.Audio.Vocoder.VocoderMode
*   DvmConsole.Platform.Audio.Vocoder.ILibVocoderNative
*   DvmConsole.Platform.Audio.Vocoder.LibVocoderVoiceCodec
*
* The adapter implements IVoiceFrameDecoder + IVoiceFrameEncoder over
* the 8-export libvocoder C ABI behind an injectable native seam. It
* is DUAL-MODE and dispatches by codeword length (9 bytes -> DMR/AMBE
* handle, 11 bytes -> P25/IMBE handle), keeping the mode-agnostic
* TalkgroupAudioRouter wiring intact. Buffer validation is EXACT
* (WPF parity VocoderInterop.cs:145-163, 291-311): wrong lengths
* throw before the native call is ever invoked.
*
* WPF parity — errs semantics (MainWindow.DMR.cs:197-217): decode
* success is shape-valid + handle-alive; errs is informational and
* there is NO threshold. TryDecode must return false ONLY for the
* native -1 NULL-handle sentinel (defensive) and shape violations;
* any errs >= 0 is success (WPF plays every frame and only logs).
* Encode has no failure return at all (void at both layers), so
* TryEncode fails only via shape/handle guards.
*
* Lifecycle: 4 handles (encoder+decoder x DMR+P25) created in the
* ctor; a Zero Create throws InvalidOperationException and rolls back
* already-created handles (MBEInterleaver parity :462-474); Dispose is
* idempotent and deletes each handle exactly once; all ops serialize
* on a single gate lock (WPF parity); use-after-dispose throws
* ObjectDisposedException.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Vocoder;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="LibVocoderVoiceCodec"/>.
    /// </summary>
    public sealed class LibVocoderVoiceCodecTests
    {
        /* ------------------------------------------------------------------
        ** Test double
        ** ---------------------------------------------------------------- */

        private sealed class FakeNative : ILibVocoderNative
        {
            public IntPtr NextHandle = (IntPtr)0x1000;
            public int NextHandleIncrement = 0x100;
            public int CreateCount;
            public int DeleteCount;
            public readonly List<VocoderMode> CreatedModes = new();
            public readonly List<IntPtr> DeletedHandles = new();
            public int DecodeResult = 0;
            public byte[]? EncodeSink;
            public short[]? DecodeSamplesSink;

            // Handles are deterministic (0x1000/0x1100/0x1200/0x1300), so
            // recording the last handle per call pins the dispatch routing:
            // Dmr encoder 0x1000, Dmr decoder 0x1100, P25 encoder 0x1200,
            // P25 decoder 0x1300.
            public IntPtr LastEncodeHandle;
            public IntPtr LastDecodeHandle;

            public IntPtr MBEEncoder_Create(VocoderMode mode)
            {
                CreateCount++;
                CreatedModes.Add(mode);
                var handle = NextHandle;
                NextHandle = (IntPtr)(NextHandle.ToInt64() + NextHandleIncrement);
                return handle;
            }

            public void MBEEncoder_Encode(IntPtr handle, short[] samples, byte[] codeword)
            {
                LastEncodeHandle = handle;
                EncodeSink = codeword;
                for (var i = 0; i < codeword.Length; i++)
                {
                    codeword[i] = 0xAB;
                }
            }

            public void MBEEncoder_EncodeBits(IntPtr handle, byte[] bits, byte[] codeword)
                => throw new NotSupportedException();

            public void MBEEncoder_Delete(IntPtr handle)
            {
                DeleteCount++;
                DeletedHandles.Add(handle);
            }

            public IntPtr MBEDecoder_Create(VocoderMode mode)
            {
                CreateCount++;
                CreatedModes.Add(mode);
                var handle = NextHandle;
                NextHandle = (IntPtr)(NextHandle.ToInt64() + NextHandleIncrement);
                return handle;
            }

            public int MBEDecoder_Decode(IntPtr handle, byte[] codeword, short[] samples)
            {
                LastDecodeHandle = handle;
                DecodeSamplesSink = samples;
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (short)(0x0102 + i);
                }

                return DecodeResult;
            }

            public int MBEDecoder_DecodeBits(IntPtr handle, byte[] bits, byte[] codeword)
                => throw new NotSupportedException();

            public void MBEDecoder_Delete(IntPtr handle)
            {
                DeleteCount++;
                DeletedHandles.Add(handle);
            }
        }

        private static readonly byte[] DmrCodeword = Enumerable.Repeat((byte)0x11, 9).ToArray();
        private static readonly byte[] P25Codeword = Enumerable.Repeat((byte)0x22, 11).ToArray();

        /* ------------------------------------------------------------------
        ** Construction
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Ctor_CreatesFourHandles_TwoPerMode()
        {
            var native = new FakeNative();
            using var codec = new LibVocoderVoiceCodec(native);

            Assert.Equal(4, native.CreateCount);
            Assert.Equal(
                new[] { VocoderMode.Dmr, VocoderMode.Dmr, VocoderMode.P25, VocoderMode.P25 },
                native.CreatedModes);
        }

        [Fact]
        public void Ctor_NullSeam_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new LibVocoderVoiceCodec(null!));
        }

        [Fact]
        public void Ctor_ZeroHandle_ThrowsInvalidOperation_AndRollsBackCreatedHandles()
        {
            var native = new FakeNative { NextHandle = IntPtr.Zero };
            Assert.Throws<InvalidOperationException>(() => new LibVocoderVoiceCodec(native));
            Assert.Equal(0, native.DeleteCount); // nothing was created before the first Zero
        }

        [Fact]
        public void Ctor_ZeroOnThirdCreate_RollsBackFirstTwoHandles()
        {
            var native = new FakeNative();
            var failOn = 3;

            // Intercept: make the 3rd Create return Zero via a wrapper that
            // delegates to a fake whose handle supplier fails on demand.
            var failing = new FailingOnCreateNative(failOn, native);
            Assert.Throws<InvalidOperationException>(() => new LibVocoderVoiceCodec(failing));

            Assert.Equal(2, native.DeleteCount); // first two handles rolled back
        }

        private sealed class FailingOnCreateNative : ILibVocoderNative
        {
            private readonly int failOn;
            private readonly FakeNative inner;
            private int count;

            public FailingOnCreateNative(int failOn, FakeNative inner)
            {
                this.failOn = failOn;
                this.inner = inner;
            }

            public IntPtr MBEEncoder_Create(VocoderMode mode)
            {
                count++;
                return count == failOn ? IntPtr.Zero : inner.MBEEncoder_Create(mode);
            }

            public IntPtr MBEDecoder_Create(VocoderMode mode)
            {
                count++;
                return count == failOn ? IntPtr.Zero : inner.MBEDecoder_Create(mode);
            }

            public void MBEEncoder_Encode(IntPtr handle, short[] samples, byte[] codeword)
                => inner.MBEEncoder_Encode(handle, samples, codeword);

            public void MBEEncoder_EncodeBits(IntPtr handle, byte[] bits, byte[] codeword)
                => inner.MBEEncoder_EncodeBits(handle, bits, codeword);

            public void MBEEncoder_Delete(IntPtr handle) => inner.MBEEncoder_Delete(handle);

            public int MBEDecoder_Decode(IntPtr handle, byte[] codeword, short[] samples)
                => inner.MBEDecoder_Decode(handle, codeword, samples);

            public int MBEDecoder_DecodeBits(IntPtr handle, byte[] bits, byte[] codeword)
                => inner.MBEDecoder_DecodeBits(handle, bits, codeword);

            public void MBEDecoder_Delete(IntPtr handle) => inner.MBEDecoder_Delete(handle);
        }

        /* ------------------------------------------------------------------
        ** Decode
        ** ---------------------------------------------------------------- */

        [Fact]
        public void TryDecode_Dmr9Bytes_True_160Samples()
        {
            var native = new FakeNative();
            using var codec = new LibVocoderVoiceCodec(native);

            var ok = codec.TryDecode(DmrCodeword, out var samples);

            Assert.True(ok);
            Assert.Equal(160, samples.Length);
            Assert.NotNull(native.DecodeSamplesSink);
            // Length dispatch must route to the DMR decoder handle (0x1100),
            // not the P25 one (0x1300) — a swapped switch would silently
            // corrupt every decoded frame.
            Assert.Equal((IntPtr)0x1100, native.LastDecodeHandle);
        }

        [Fact]
        public void TryDecode_P25_11Bytes_True()
        {
            var native = new FakeNative();
            using var codec = new LibVocoderVoiceCodec(native);

            Assert.True(codec.TryDecode(P25Codeword, out var samples));
            Assert.Equal(160, samples.Length);
            Assert.Equal((IntPtr)0x1300, native.LastDecodeHandle); // P25 decoder
        }

        [Fact]
        public void TryDecode_NativeMinusOne_False()
        {
            var native = new FakeNative { DecodeResult = -1 };
            using var codec = new LibVocoderVoiceCodec(native);

            Assert.False(codec.TryDecode(DmrCodeword, out _));
        }

        [Fact]
        public void TryDecode_ErrsZeroAndPositive_AlwaysTrue_NoThreshold()
        {
            // WPF parity: errs is informational, no cutoff (MainWindow.DMR.cs
            // plays every frame and only logs). Pinned so a future edit cannot
            // "improve" TryDecode with a threshold that silently drops voice.
            foreach (var errs in new[] { 0, 5, 99 })
            {
                var native = new FakeNative { DecodeResult = errs };
                using var codec = new LibVocoderVoiceCodec(native);
                Assert.True(codec.TryDecode(DmrCodeword, out _), $"errs={errs}");
            }
        }

        [Fact]
        public void TryDecode_WrongLength_Throws_NativeNeverInvoked()
        {
            var native = new FakeNative();
            using var codec = new LibVocoderVoiceCodec(native);

            foreach (var length in new[] { 8, 10, 12, 27 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    codec.TryDecode(new byte[length], out _));
            }

            Assert.Null(native.DecodeSamplesSink); // native never called
        }

        [Fact]
        public void TryDecode_NullMemory_Throws()
        {
            var native = new FakeNative();
            using var codec = new LibVocoderVoiceCodec(native);

            Assert.Throws<ArgumentNullException>(() =>
                codec.TryDecode(ReadOnlyMemory<byte>.Empty, out _));
        }

        /* ------------------------------------------------------------------
        ** Encode
        ** ---------------------------------------------------------------- */

        [Fact]
        public void TryEncode_160Samples_Dmr_True_9ByteCodeword()
        {
            var native = new FakeNative();
            using var codec = new LibVocoderVoiceCodec(native);

            var ok = codec.TryEncode(VoiceMode.Dmr, new short[160], out var codeword);

            Assert.True(ok);
            Assert.Equal(9, codeword.Length);
            Assert.Equal((IntPtr)0x1000, native.LastEncodeHandle); // DMR encoder
        }

        [Fact]
        public void TryEncode_160Samples_P25_True_11ByteCodeword()
        {
            // The encode seam is MODE-AWARE (unlike decode, which dispatches
            // by codeword length): the native handle is mode-bound, so the
            // caller supplies the mode (the router passes the transmit
            // session's target mode). P25 -> 11-byte codeword.
            var native = new FakeNative();
            using var codec = new LibVocoderVoiceCodec(native);

            var ok = codec.TryEncode(VoiceMode.P25, new short[160], out var codeword);

            Assert.True(ok);
            Assert.Equal(11, codeword.Length);
            Assert.Equal((IntPtr)0x1200, native.LastEncodeHandle); // P25 encoder
        }

        [Fact]
        public void TryEncode_DmrThenP25_UsesPerModeHandles()
        {
            // Both modes through one adapter must reach the matching native
            // handles: the fake records the handle per call, and the
            // deterministic allocation order pins the dispatch (Dmr encoder
            // 0x1000, P25 encoder 0x1200).
            var native = new FakeNative();
            using var codec = new LibVocoderVoiceCodec(native);

            codec.TryEncode(VoiceMode.Dmr, new short[160], out var dmrCode);
            Assert.Equal((IntPtr)0x1000, native.LastEncodeHandle);

            codec.TryEncode(VoiceMode.P25, new short[160], out var p25Code);
            Assert.Equal((IntPtr)0x1200, native.LastEncodeHandle);

            Assert.Equal(9, dmrCode.Length);
            Assert.Equal(11, p25Code.Length);
        }

        [Fact]
        public void TryEncode_WrongSampleCount_Throws_NativeNeverInvoked()
        {
            var native = new FakeNative();
            using var codec = new LibVocoderVoiceCodec(native);

            foreach (var length in new[] { 159, 161, 320 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    codec.TryEncode(VoiceMode.Dmr, new short[length], out _));
            }

            Assert.Null(native.EncodeSink); // native never called
        }

        [Fact]
        public void TryEncode_NullSamples_Throws()
        {
            var native = new FakeNative();
            using var codec = new LibVocoderVoiceCodec(native);

            Assert.Throws<ArgumentNullException>(() =>
                codec.TryEncode(VoiceMode.Dmr, ReadOnlyMemory<short>.Empty, out _));
        }

        /* ------------------------------------------------------------------
        ** Lifecycle
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Dispose_DeletesEachHandleExactlyOnce()
        {
            var native = new FakeNative();
            var codec = new LibVocoderVoiceCodec(native);

            codec.Dispose();

            Assert.Equal(4, native.DeleteCount);
            Assert.Equal(4, native.DeletedHandles.Distinct().Count());
        }

        [Fact]
        public void Dispose_Twice_Idempotent()
        {
            var native = new FakeNative();
            var codec = new LibVocoderVoiceCodec(native);

            codec.Dispose();
            codec.Dispose();

            Assert.Equal(4, native.DeleteCount); // no double-delete
        }

        [Fact]
        public void UseAfterDispose_ThrowsObjectDisposed()
        {
            var native = new FakeNative();
            var codec = new LibVocoderVoiceCodec(native);
            codec.Dispose();

            Assert.Throws<ObjectDisposedException>(() => codec.TryDecode(DmrCodeword, out _));
            Assert.Throws<ObjectDisposedException>(() => codec.TryEncode(VoiceMode.Dmr, new short[160], out _));
        }

        /* ------------------------------------------------------------------
        ** Thread-safety
        ** ---------------------------------------------------------------- */

        [Fact]
        public async Task ConcurrentDecodeEncodeAndDispose_NoDoubleDelete_NoCrash()
        {
            var native = new FakeNative();
            var codec = new LibVocoderVoiceCodec(native);

            var tasks = new List<Task>();
            for (var i = 0; i < 8; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        for (var j = 0; j < 200; j++)
                        {
                            codec.TryDecode(DmrCodeword, out _);
                            codec.TryEncode(VoiceMode.Dmr, new short[160], out _);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // expected once disposed
                    }
                }));
            }

            await Task.Delay(20);
            codec.Dispose();
            await Task.WhenAll(tasks);

            Assert.Equal(4, native.DeleteCount); // exactly once per handle
        }

        /* ------------------------------------------------------------------
        ** Seam shape
        ** ---------------------------------------------------------------- */

        [Fact]
        public void LibVocoderNative_OffMacOs_CtorNoOp_AllCallsThrowPlatformNotSupported()
        {
            // On this Linux host OperatingSystem.IsMacOS() is false: the
            // production seam must construct harmlessly and every C-ABI call
            // must throw PlatformNotSupportedException (never resolve native).
            using var native = new LibVocoderNative();

            Assert.Throws<PlatformNotSupportedException>(() => native.MBEEncoder_Create(VocoderMode.Dmr));
            Assert.Throws<PlatformNotSupportedException>(() => native.MBEEncoder_Encode(IntPtr.Zero, new short[160], new byte[9]));
            Assert.Throws<PlatformNotSupportedException>(() => native.MBEEncoder_EncodeBits(IntPtr.Zero, new byte[49], new byte[9]));
            Assert.Throws<PlatformNotSupportedException>(() => native.MBEEncoder_Delete(IntPtr.Zero));
            Assert.Throws<PlatformNotSupportedException>(() => native.MBEDecoder_Create(VocoderMode.P25));
            Assert.Throws<PlatformNotSupportedException>(() => native.MBEDecoder_Decode(IntPtr.Zero, new byte[9], new short[160]));
            Assert.Throws<PlatformNotSupportedException>(() => native.MBEDecoder_DecodeBits(IntPtr.Zero, new byte[49], new byte[11]));
            Assert.Throws<PlatformNotSupportedException>(() => native.MBEDecoder_Delete(IntPtr.Zero));
        }

        [Fact]
        public void VocoderMode_HasDmrAndP25_InOrder()
        {
            Assert.Equal(new[] { "Dmr", "P25" }, Enum.GetNames(typeof(VocoderMode)));
            Assert.Equal(0, (int)VocoderMode.Dmr);
            Assert.Equal(1, (int)VocoderMode.P25);
        }

        [Fact]
        public void LibVocoderVoiceCodec_SurfaceIsExact()
        {
            var type = typeof(LibVocoderVoiceCodec);
            Assert.True(type.IsSealed);
            Assert.True(typeof(IDisposable).IsAssignableFrom(type));
            Assert.True(typeof(IVoiceFrameDecoder).IsAssignableFrom(type));
            Assert.True(typeof(IVoiceFrameEncoder).IsAssignableFrom(type));
        }

        [Fact]
        public void IlLibVocoderNative_HasExactEightMethods()
        {
            var methods = typeof(ILibVocoderNative)
                .GetMethods()
                .Select(m => m.Name)
                .OrderBy(n => n)
                .ToArray();
            Assert.Equal(
                new[]
                {
                    "MBEDecoder_Create", "MBEDecoder_Decode", "MBEDecoder_DecodeBits",
                    "MBEDecoder_Delete", "MBEEncoder_Create", "MBEEncoder_Delete",
                    "MBEEncoder_Encode", "MBEEncoder_EncodeBits",
                },
                methods);
        }
    }
}
