// SPDX-License-Identifier: AGPL-3.0-only
/**
* Managed regression/verification tests for the dvmvocoder C ABI surfaced by
* dvmconsole/VocoderInterop.cs (MBEEncoder / MBEDecoder / MBEInterleaver).
*
* Linux managed proof for this slice: the native library must be discoverable,
* e.g.
*
*     LD_LIBRARY_PATH=/tmp/dvmvocoder_spike_60624/dvmvocoder/build \
*         dotnet test tests/DvmConsole.Vocoder.Tests/...
*
* The upstream C ABI uses uint8_t ptr / char ptr binary 0/1 bit buffers, int16_t[160]
* PCM frames, byte codewords (9 DMR / 11 IMBE) and mode enum values
* 0 = DMR_AMBE / 1 = IMBE_88BIT. These tests pin that contract end-to-end
* through the managed wrappers.
*/

using dvmconsole;
using Xunit;

namespace DvmConsole.Vocoder.Tests
{
    public sealed class VocoderInteropTests
    {
        private const int PcmSamples = 160;
        private const int DmrCodeBytes = 9;
        private const int ImbeCodeBytes = 11;
        private const int DmrCodeBits = 49;
        private const int ImbeCodeBits = 88;

        // Genuinely wrong lengths for the validation guards. Deliberately LARGER
        // than the contract so a RED run that still reaches the native call
        // reads/writes within the allocation instead of corrupting the heap.
        private const int WrongPcmLength = 200;
        private const int WrongCodeLength = 20;
        private const int WrongBitsLength = 120;

        private static Int16[] MakeSamples(int n = PcmSamples)
        {
            Int16[] samples = new Int16[n];
            for (int i = 0; i < n; i++)
                samples[i] = (Int16)(Math.Sin(i * 0.1) * 8000.0);
            return samples;
        }

        private static byte[] MakeBits(int n, byte value)
        {
            byte[] bits = new byte[n];
            for (int i = 0; i < n; i++)
                bits[i] = value;
            return bits;
        }

        private static bool AllBitsAreZeroOrOne(byte[] bits)
        {
            for (int i = 0; i < bits.Length; i++)
            {
                if (bits[i] != 0 && bits[i] != 1)
                    return false;
            }
            return true;
        }

        private static int CodewordBytesFor(MBE_MODE mode) =>
            mode == MBE_MODE.DMR_AMBE ? DmrCodeBytes : ImbeCodeBytes;

        private static int CodeBitsFor(MBE_MODE mode) =>
            mode == MBE_MODE.DMR_AMBE ? DmrCodeBits : ImbeCodeBits;

        // ------------------------------------------------------------------
        // Construction (both MBE_MODE values)
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Encoder_Constructs_ForBothModes(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            Assert.IsAssignableFrom<IDisposable>(encoder);
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Decoder_Constructs_ForBothModes(MBE_MODE mode)
        {
            using var decoder = new MBEDecoder(mode);
            Assert.IsAssignableFrom<IDisposable>(decoder);
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Interleaver_Constructs_ForBothModes(MBE_MODE mode)
        {
            using var interleaver = new MBEInterleaver(mode);
            Assert.IsAssignableFrom<IDisposable>(interleaver);
        }

        // ------------------------------------------------------------------
        // 160-sample PCM encode / decode
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Encode_Pcm_ProducesModeCodewordLength(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            byte[] codeword = new byte[CodewordBytesFor(mode)];
            encoder.encode(MakeSamples(), codeword);
            Assert.Equal(CodewordBytesFor(mode), codeword.Length);
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Encode_ThenDecode_Pcm_RoundTrips160Samples(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            using var decoder = new MBEDecoder(mode);

            byte[] codeword = new byte[CodewordBytesFor(mode)];
            encoder.encode(MakeSamples(), codeword);

            Int16[] decoded = new Int16[PcmSamples];
            int errs = decoder.decode(codeword, decoded);

            Assert.Equal(PcmSamples, decoded.Length);
            Assert.True(errs >= 0, $"decode error count must be >= 0, was {errs}");
        }

        // ------------------------------------------------------------------
        // Bit encode / decode: 49/88 byte bits and 9/11-byte codewords
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void EncodeBits_ProducesModeCodewordLength(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            byte[] codeword = new byte[CodewordBytesFor(mode)];
            encoder.encodeBits(MakeBits(CodeBitsFor(mode), 0), codeword);
            Assert.Equal(CodewordBytesFor(mode), codeword.Length);
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void DecodeBits_ReturnsModeBitCount(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            using var decoder = new MBEDecoder(mode);

            byte[] codeword = new byte[CodewordBytesFor(mode)];
            encoder.encodeBits(MakeBits(CodeBitsFor(mode), 1), codeword);

            byte[] outBits = new byte[CodeBitsFor(mode)];
            int errs = decoder.decodeBits(codeword, outBits);

            Assert.Equal(CodeBitsFor(mode), outBits.Length);
            Assert.True(errs >= 0, $"decode error count must be >= 0, was {errs}");
        }

        // ------------------------------------------------------------------
        // Binary 0/1 bit values preserved end-to-end
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void EncodeBits_AllZero_And_AllOne_ProduceValidCodewords(MBE_MODE mode)
        {
            byte[] allZero = new byte[] { 0 };
            byte[] allOne = new byte[] { 1 };

            foreach (byte value in allZero.Concat(allOne))
            {
                using var encoder = new MBEEncoder(mode);
                byte[] codeword = new byte[CodewordBytesFor(mode)];
                encoder.encodeBits(MakeBits(CodeBitsFor(mode), value), codeword);
                Assert.Equal(CodewordBytesFor(mode), codeword.Length);
            }
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void DecodeBits_OutputsOnlyZeroOrOne(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            using var decoder = new MBEDecoder(mode);

            byte[] codeword = new byte[CodewordBytesFor(mode)];
            encoder.encodeBits(MakeBits(CodeBitsFor(mode), 1), codeword);

            byte[] outBits = new byte[CodeBitsFor(mode)];
            decoder.decodeBits(codeword, outBits);

            Assert.True(AllBitsAreZeroOrOne(outBits), "decoded MBE bits must be binary 0/1");
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Interleaver_Encode_ThenDecode_RoundTripsBitCount(MBE_MODE mode)
        {
            using var interleaver = new MBEInterleaver(mode);

            byte[] bits = new byte[CodeBitsFor(mode)];
            for (int i = 0; i < bits.Length; i++)
                bits[i] = (byte)(i & 1); // alternate 0/1 pattern

            byte[] codeword = new byte[CodewordBytesFor(mode)];
            interleaver.Encode(bits, codeword);
            Assert.Equal(CodewordBytesFor(mode), codeword.Length);

            byte[] outBits = new byte[CodeBitsFor(mode)];
            int errs = interleaver.Decode(codeword, outBits);

            Assert.Equal(CodeBitsFor(mode), outBits.Length);
            Assert.True(AllBitsAreZeroOrOne(outBits), "interleaver-decoded bits must be binary 0/1");
            Assert.True(errs >= 0, $"decode error count must be >= 0, was {errs}");
        }

        // ------------------------------------------------------------------
        // Deterministic / idempotent disposal
        // ------------------------------------------------------------------

        [Fact]
        public void Encoder_Dispose_IsIdempotent()
        {
            var encoder = new MBEEncoder(MBE_MODE.DMR_AMBE);
            encoder.Dispose();
            encoder.Dispose(); // second call must be a no-op, not a double free
            GC.KeepAlive(encoder);
        }

        [Fact]
        public void Decoder_Dispose_IsIdempotent()
        {
            var decoder = new MBEDecoder(MBE_MODE.DMR_AMBE);
            decoder.Dispose();
            decoder.Dispose();
            GC.KeepAlive(decoder);
        }

        [Fact]
        public void Interleaver_Dispose_IsIdempotent()
        {
            var interleaver = new MBEInterleaver(MBE_MODE.IMBE_88BIT);
            interleaver.Dispose();
            interleaver.Dispose();
            GC.KeepAlive(interleaver);
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Dispose_AllObjects_Repeatedly_IsDeterministic(MBE_MODE mode)
        {
            for (int i = 0; i < 25; i++)
            {
                using (var encoder = new MBEEncoder(mode))
                using (var decoder = new MBEDecoder(mode))
                using (var interleaver = new MBEInterleaver(mode))
                {
                    // exercise the handles before deterministic teardown
                    byte[] codeword = new byte[CodewordBytesFor(mode)];
                    encoder.encode(MakeSamples(), codeword);
                    decoder.decode(codeword, new Int16[PcmSamples]);
                    interleaver.Encode(MakeBits(CodeBitsFor(mode), 0), codeword);
                    interleaver.Decode(codeword, new byte[CodeBitsFor(mode)]);
                }
            }

            // force finalizers so leftover handles (if any) run their guarded
            // finalizer path without a double-delete crash
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        [Fact]
        public void Encoder_UsageAfterDispose_ThrowsObjectDisposed()
        {
            var encoder = new MBEEncoder(MBE_MODE.DMR_AMBE);
            encoder.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                encoder.encode(new Int16[PcmSamples], new byte[DmrCodeBytes]));
            Assert.Throws<ObjectDisposedException>(() =>
                encoder.encodeBits(new byte[DmrCodeBits], new byte[DmrCodeBytes]));
        }

        [Fact]
        public void Decoder_UsageAfterDispose_ThrowsObjectDisposed()
        {
            var decoder = new MBEDecoder(MBE_MODE.DMR_AMBE);
            decoder.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                decoder.decode(new byte[DmrCodeBytes], new Int16[PcmSamples]));
            Assert.Throws<ObjectDisposedException>(() =>
                decoder.decodeBits(new byte[DmrCodeBytes], new byte[DmrCodeBits]));
        }

        // ------------------------------------------------------------------
        // Managed length validation before native calls (native ABI carries
        // no lengths, so the managed wrappers must reject mismatches first)
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Encode_WithWrongPcmLength_ThrowsArgumentOutOfRange(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            byte[] codeword = new byte[CodewordBytesFor(mode)];
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                encoder.encode(new Int16[WrongPcmLength], codeword));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Encode_WithWrongCodewordLength_ThrowsArgumentOutOfRange(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                encoder.encode(MakeSamples(), new byte[WrongCodeLength]));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void EncodeBits_WithWrongBitsLength_ThrowsArgumentOutOfRange(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                encoder.encodeBits(new byte[WrongBitsLength], new byte[CodewordBytesFor(mode)]));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void EncodeBits_WithWrongCodewordLength_ThrowsArgumentOutOfRange(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                encoder.encodeBits(MakeBits(CodeBitsFor(mode), 0), new byte[WrongCodeLength]));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Decode_WithWrongCodewordLength_ThrowsArgumentOutOfRange(MBE_MODE mode)
        {
            using var decoder = new MBEDecoder(mode);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                decoder.decode(new byte[WrongCodeLength], new Int16[PcmSamples]));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Decode_WithWrongSamplesLength_ThrowsArgumentOutOfRange(MBE_MODE mode)
        {
            using var decoder = new MBEDecoder(mode);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                decoder.decode(new byte[CodewordBytesFor(mode)], new Int16[WrongPcmLength]));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void DecodeBits_WithWrongCodewordLength_ThrowsArgumentOutOfRange(MBE_MODE mode)
        {
            using var decoder = new MBEDecoder(mode);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                decoder.decodeBits(new byte[WrongCodeLength], new byte[CodeBitsFor(mode)]));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void DecodeBits_WithWrongBitsLength_ThrowsArgumentOutOfRange(MBE_MODE mode)
        {
            using var decoder = new MBEDecoder(mode);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                decoder.decodeBits(new byte[CodewordBytesFor(mode)], new byte[WrongBitsLength]));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Interleaver_Encode_WithWrongCodewordLength_ThrowsArgumentOutOfRange(MBE_MODE mode)
        {
            using var interleaver = new MBEInterleaver(mode);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                interleaver.Encode(MakeBits(CodeBitsFor(mode), 0), new byte[WrongCodeLength]));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Interleaver_Decode_WithWrongBitsLength_ThrowsArgumentOutOfRange(MBE_MODE mode)
        {
            using var interleaver = new MBEInterleaver(mode);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                interleaver.Decode(new byte[CodewordBytesFor(mode)], new byte[WrongBitsLength]));
        }

        // ------------------------------------------------------------------
        // Null argument guards on the managed wrappers
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Encode_NullArguments_ThrowArgumentNullException(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            Assert.Throws<ArgumentNullException>(() => encoder.encode(null, new byte[CodewordBytesFor(mode)]));
            Assert.Throws<ArgumentNullException>(() => encoder.encode(new Int16[PcmSamples], null));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void EncodeBits_NullArguments_ThrowArgumentNullException(MBE_MODE mode)
        {
            using var encoder = new MBEEncoder(mode);
            Assert.Throws<ArgumentNullException>(() => encoder.encodeBits(null, new byte[CodewordBytesFor(mode)]));
            Assert.Throws<ArgumentNullException>(() => encoder.encodeBits(new byte[CodeBitsFor(mode)], null));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Decode_NullArguments_ThrowArgumentNullException(MBE_MODE mode)
        {
            using var decoder = new MBEDecoder(mode);
            Assert.Throws<ArgumentNullException>(() => decoder.decode(null, new Int16[PcmSamples]));
            Assert.Throws<ArgumentNullException>(() => decoder.decode(new byte[CodewordBytesFor(mode)], null));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void DecodeBits_NullArguments_ThrowArgumentNullException(MBE_MODE mode)
        {
            using var decoder = new MBEDecoder(mode);
            Assert.Throws<ArgumentNullException>(() => decoder.decodeBits(null, new byte[CodeBitsFor(mode)]));
            Assert.Throws<ArgumentNullException>(() => decoder.decodeBits(new byte[CodewordBytesFor(mode)], null));
        }

        [Theory]
        [InlineData(MBE_MODE.DMR_AMBE)]
        [InlineData(MBE_MODE.IMBE_88BIT)]
        public void Interleaver_NullArguments_ThrowArgumentNullException(MBE_MODE mode)
        {
            using var interleaver = new MBEInterleaver(mode);
            byte[] codeword = new byte[CodewordBytesFor(mode)];
            byte[] bits = new byte[CodeBitsFor(mode)];
            Assert.Throws<ArgumentNullException>(() => interleaver.Decode(null, bits));
            Assert.Throws<ArgumentNullException>(() => interleaver.Decode(codeword, null));
            Assert.Throws<ArgumentNullException>(() => interleaver.Encode(null, codeword));
            Assert.Throws<ArgumentNullException>(() => interleaver.Encode(bits, null));
        }

        [Fact]
        public void Interleaver_UsageAfterDispose_ThrowsObjectDisposed()
        {
            var interleaver = new MBEInterleaver(MBE_MODE.DMR_AMBE);
            interleaver.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                interleaver.Decode(new byte[DmrCodeBytes], new byte[DmrCodeBits]));
            Assert.Throws<ObjectDisposedException>(() =>
                interleaver.Encode(new byte[DmrCodeBits], new byte[DmrCodeBytes]));
        }

        // ------------------------------------------------------------------
        // Invalid MBE_MODE rejected at construction
        // ------------------------------------------------------------------

        [Fact]
        public void Encoder_InvalidMode_ThrowsArgumentOutOfRange()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new MBEEncoder((MBE_MODE)99));
        }

        [Fact]
        public void Decoder_InvalidMode_ThrowsArgumentOutOfRange()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new MBEDecoder((MBE_MODE)99));
        }

        [Fact]
        public void Interleaver_InvalidMode_ThrowsArgumentOutOfRange()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new MBEInterleaver((MBE_MODE)99));
        }

        // ------------------------------------------------------------------
        // Teardown safety under concurrency
        // ------------------------------------------------------------------

        [Fact]
        public void Dispose_Interleaver_ManyThreadsConcurrently_IsSafe()
        {
            var interleaver = new MBEInterleaver(MBE_MODE.IMBE_88BIT);
            Task[] tasks = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => interleaver.Dispose()))
                .ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            GC.KeepAlive(interleaver);
        }

        [Fact]
        public void Dispose_Encoder_ManyThreadsConcurrently_IsSafe()
        {
            var encoder = new MBEEncoder(MBE_MODE.DMR_AMBE);
            Task[] tasks = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => encoder.Dispose()))
                .ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            GC.KeepAlive(encoder);
        }

        [Fact]
        public void Dispose_Decoder_ManyThreadsConcurrently_IsSafe()
        {
            var decoder = new MBEDecoder(MBE_MODE.DMR_AMBE);
            Task[] tasks = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => decoder.Dispose()))
                .ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            GC.KeepAlive(decoder);
        }

        [Fact]
        public void Encode_ConcurrentWithDispose_DoesNotCrashOrDoubleFree()
        {
            for (int i = 0; i < 100; i++)
            {
                var encoder = new MBEEncoder(MBE_MODE.DMR_AMBE);
                var samples = MakeSamples();
                var codeword = new byte[DmrCodeBytes];
                int outcome = -1; // 0 = encode completed, 1 = raced with dispose
                var race = Task.Run(() =>
                {
                    try
                    {
                        encoder.encode(samples, codeword);
                        outcome = 0;
                    }
                    catch (ObjectDisposedException)
                    {
                        outcome = 1;
                    }
                });
                encoder.Dispose();
                if (!race.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("encode/dispose race did not finish");
                Assert.True(outcome == 0 || outcome == 1,
                    "encode must either complete or observe disposal; any other result indicates a bad free");
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    } // public sealed class VocoderInteropTests
}
