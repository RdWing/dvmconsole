// SPDX-License-Identifier: AGPL-3.0-only
/**
* Live consumer compatibility tests for MBEToneGenerator.IMBEEncodeSingleTone
* (linked from dvmconsole/VocoderInterop.cs), exercising the nearest-tone
* selection and buffer contract against the Core-owned
* VocoderToneLookupTable.
*
* The linked production interop resolves VocoderToneLookupTable through the
* portable DvmConsole.Core ProjectReference, so these tests exercise the real
* 72-entry table and preserve the existing consumer behavior.
*
* Expected bytes are the public single-tone codewords from the production
* table (recorded from an EF Johnson VP8000); no secrets.
*/
using dvmconsole;
using Xunit;

namespace DvmConsole.Vocoder.Tests
{
    /// <summary>
    /// Compatibility tests for the IMBE single-tone encoder.
    /// </summary>
    public sealed class MBEToneGeneratorTests
    {
        // Known codewords straight from VocoderToneLookupTable.
        private static readonly byte[] Tone281 =
            { 0x10, 0xFF, 0xF9, 0x45, 0x31, 0xCC, 0x8A, 0xC4, 0x3C, 0x16, 0x4B };
        private static readonly byte[] Tone313 =
            { 0x0C, 0xFF, 0xF9, 0x45, 0x31, 0xCC, 0x8A, 0xC4, 0x3C, 0x17, 0x48 };
        private static readonly byte[] Tone1000 =
            { 0x09, 0x23, 0x0B, 0x0D, 0xC4, 0xA5, 0xCA, 0xE8, 0x28, 0x0A, 0x32 };
        private static readonly byte[] Tone1031 =
            { 0x05, 0x51, 0xFB, 0xCA, 0xCE, 0x49, 0xCC, 0x03, 0x25, 0x59, 0x97 };
        private static readonly byte[] Tone2500 =
            { 0x05, 0x06, 0xFB, 0x63, 0xCD, 0xD9, 0x2B, 0x42, 0xE1, 0xCF, 0x6B };

        private static byte[] Encode(ushort hz)
        {
            byte[] codeword = new byte[11];
            MBEToneGenerator.IMBEEncodeSingleTone(hz, codeword);
            return codeword;
        }

        /// <summary>
        /// An exact table frequency encodes to its own table entry.
        /// </summary>
        [Fact]
        public void Encode_Exact1000Hz_OutputsTableEntryFor1000()
        {
            Assert.Equal(Tone1000, Encode(1000));
        }

        /// <summary>
        /// 1015 Hz is 15 Hz from 1000 and 16 Hz from 1031: nearest wins.
        /// </summary>
        [Fact]
        public void Encode_1015Hz_ChoosesNearestLower1000()
        {
            Assert.Equal(Tone1000, Encode(1015));
        }

        /// <summary>
        /// 1016 Hz is 16 Hz from 1000 and 15 Hz from 1031: nearest wins.
        /// </summary>
        [Fact]
        public void Encode_1016Hz_ChoosesNearestUpper1031()
        {
            Assert.Equal(Tone1031, Encode(1016));
        }

        /// <summary>
        /// 297 Hz is exactly halfway between 281 and 313. The production
        /// nearest-key scan is
        /// Keys.Aggregate((x, y) => Math.Abs(x - f) < Math.Abs(y - f) ? x : y),
        /// whose strict comparison takes the incoming y on a tie; the first
        /// 281-vs-313 comparison therefore resolves to 313, and every later
        /// key is farther away. The live behavior is 313 -- lock it here.
        /// </summary>
        [Fact]
        public void Encode_297Hz_TieBetween281And313_ResolvesToHigher313()
        {
            Assert.Equal(Tone313, Encode(297));
        }

        /// <summary>
        /// Below the lowest table key, the lowest entry (281 Hz) wins.
        /// </summary>
        [Fact]
        public void Encode_100Hz_ClampsToLowest281()
        {
            Assert.Equal(Tone281, Encode(100));
        }

        /// <summary>
        /// Above the highest table key, the highest entry (2500 Hz) wins.
        /// </summary>
        [Fact]
        public void Encode_5000Hz_ClampsToHighest2500()
        {
            Assert.Equal(Tone2500, Encode(5000));
        }

        /// <summary>
        /// The 11-byte codeword is written at the start of the output buffer:
        /// a 16-byte buffer prefilled 0xAA has bytes 0..10 overwritten and
        /// bytes 11..15 left untouched.
        /// </summary>
        [Fact]
        public void Encode_16BytePrefilledBuffer_OverwritesOnlyFirst11Bytes()
        {
            byte[] codeword = new byte[16];
            for (int i = 0; i < codeword.Length; i++)
                codeword[i] = 0xAA;

            MBEToneGenerator.IMBEEncodeSingleTone(1000, codeword);

            Assert.Equal(Tone1000, codeword.Take(11).ToArray());
            for (int i = 11; i < codeword.Length; i++)
                Assert.Equal(0xAA, codeword[i]);
        }

        /// <summary>
        /// A destination smaller than the 11-byte codeword is rejected by
        /// Array.Copy.
        /// </summary>
        [Fact]
        public void Encode_10ByteBuffer_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                MBEToneGenerator.IMBEEncodeSingleTone(1000, new byte[10]));
        }

        /// <summary>
        /// A null destination is rejected.
        /// </summary>
        [Fact]
        public void Encode_NullBuffer_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                MBEToneGenerator.IMBEEncodeSingleTone(1000, null));
        }
    }
}
