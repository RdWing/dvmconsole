// SPDX-License-Identifier: AGPL-3.0-only
/**
* Deterministic contract tests for dvmconsole.VocoderToneLookupTable
* (DvmConsole.Core). These lock the public shape of the tone lookup table
* the vocoder slice consumes: a mutable static SortedDictionary<ushort,
* byte[]> field of 72 entries spanning 281..2500 Hz, keyed by
* round-half-up(31.25 * n) for n = 9..80, with every value an 11-byte IMBE
* tone codeword.
*
* The type is owned by DvmConsole.Core; these tests protect that extraction
* boundary and the exact persisted audio-data contract.
*
* The byte vectors below are public single-tone audio codewords captured
* from an EF Johnson VP8000 (see the production file's header); no secrets.
*/
using System.Reflection;
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Contract tests for the shared IMBE tone lookup table.
    /// </summary>
    public sealed class VocoderToneLookupTableTests
    {
        /// <summary>
        /// The type is a public, non-static class living in the portable
        /// DvmConsole.Core assembly, in the dvmconsole namespace the vocoder
        /// slice already imports.
        /// </summary>
        [Fact]
        public void Type_IsPublicNonStaticClassInDvmConsoleCore()
        {
            Assert.True(typeof(VocoderToneLookupTable).IsClass);
            Assert.True(typeof(VocoderToneLookupTable).IsPublic);
            Assert.False(typeof(VocoderToneLookupTable).IsAbstract);
            Assert.Equal("dvmconsole", typeof(VocoderToneLookupTable).Namespace);
            Assert.Equal("DvmConsole.Core", typeof(VocoderToneLookupTable).Assembly.GetName().Name);
        }

        /// <summary>
        /// IMBEToneFrames must be a public static mutable field of the exact
        /// type SortedDictionary&lt;ushort, byte[]&gt; -- not a property and not
        /// init-only. A property named IMBEToneFrames would not be found by
        /// GetField, and a readonly field would trip the IsInitOnly guard.
        /// </summary>
        [Fact]
        public void IMBEToneFrames_IsPublicStaticMutableSortedDictionaryField()
        {
            FieldInfo field = typeof(VocoderToneLookupTable).GetField("IMBEToneFrames");
            Assert.NotNull(field);
            Assert.True(field.IsPublic);
            Assert.True(field.IsStatic);
            Assert.False(field.IsInitOnly);
            Assert.Equal(typeof(SortedDictionary<ushort, byte[]>), field.FieldType);
            Assert.Null(typeof(VocoderToneLookupTable).GetProperty("IMBEToneFrames"));
        }

        /// <summary>
        /// The table holds exactly 72 tone codewords.
        /// </summary>
        [Fact]
        public void Table_HasExactly72Entries()
        {
            Assert.Equal(72, VocoderToneLookupTable.IMBEToneFrames.Count);
        }

        /// <summary>
        /// Keys are round-half-up(31.25 * n) for n = 9..80, computed with
        /// integer arithmetic only: 31.25 * n == 125n/4, and adding 2 before
        /// the integer division implements round-half-up exactly. No floating
        /// point, so the formula is unambiguous on every runtime.
        /// </summary>
        [Fact]
        public void Table_KeysMatchRoundHalfUp31_25Formula()
        {
            List<ushort> expected = new List<ushort>(72);
            for (int n = 9; n <= 80; n++)
                expected.Add((ushort)((125 * n + 2) / 4));

            Assert.Equal(expected, VocoderToneLookupTable.IMBEToneFrames.Keys.ToList());
        }

        /// <summary>
        /// Keys are strictly ascending, starting at 281 Hz and ending at
        /// 2500 Hz.
        /// </summary>
        [Fact]
        public void Table_KeysStrictlyAscending_WithMin281Max2500()
        {
            List<ushort> keys = VocoderToneLookupTable.IMBEToneFrames.Keys.ToList();

            Assert.Equal(281, keys[0]);
            Assert.Equal(2500, keys[keys.Count - 1]);
            for (int i = 1; i < keys.Count; i++)
                Assert.True(keys[i] > keys[i - 1],
                    $"key {keys[i]} at index {i} must be greater than previous key {keys[i - 1]}");
        }

        /// <summary>
        /// Every value is exactly one 11-byte IMBE tone codeword.
        /// </summary>
        [Fact]
        public void Table_EveryValueIsExactly11Bytes()
        {
            foreach (KeyValuePair<ushort, byte[]> entry in VocoderToneLookupTable.IMBEToneFrames)
                Assert.True(entry.Value.Length == 11,
                    $"entry at key {entry.Key} must be 11 bytes, was {entry.Value.Length}");
        }

        /// <summary>
        /// Known-answer vectors at representative keys across the range.
        /// </summary>
        public static IEnumerable<object[]> KnownAnswerVectors()
        {
            yield return new object[]
            {
                (ushort)281,
                new byte[] { 0x10, 0xFF, 0xF9, 0x45, 0x31, 0xCC, 0x8A, 0xC4, 0x3C, 0x16, 0x4B }
            };
            yield return new object[]
            {
                (ushort)500,
                new byte[] { 0x18, 0xDF, 0x94, 0x2A, 0x5F, 0x28, 0x86, 0x20, 0x0B, 0xF6, 0xF2 }
            };
            yield return new object[]
            {
                (ushort)1000,
                new byte[] { 0x09, 0x23, 0x0B, 0x0D, 0xC4, 0xA5, 0xCA, 0xE8, 0x28, 0x0A, 0x32 }
            };
            yield return new object[]
            {
                (ushort)1500,
                new byte[] { 0x01, 0x2D, 0xA7, 0x2A, 0xDD, 0xA8, 0x5C, 0xC8, 0x5C, 0x49, 0x46 }
            };
            yield return new object[]
            {
                (ushort)2000,
                new byte[] { 0x01, 0x2C, 0xA2, 0xA2, 0x55, 0x01, 0x53, 0x0C, 0x92, 0x83, 0x2A }
            };
            yield return new object[]
            {
                (ushort)2500,
                new byte[] { 0x05, 0x06, 0xFB, 0x63, 0xCD, 0xD9, 0x2B, 0x42, 0xE1, 0xCF, 0x6B }
            };
        }

        [Theory]
        [MemberData(nameof(KnownAnswerVectors))]
        public void Table_KnownAnswerVectors(ushort key, byte[] expected)
        {
            Assert.Equal(expected, VocoderToneLookupTable.IMBEToneFrames[key]);
        }

        /// <summary>
        /// The static field and its values are shared: repeated lookups must
        /// return the same SortedDictionary instance and the same byte[]
        /// references, never fresh copies.
        /// </summary>
        [Fact]
        public void Table_RepeatedLookup_ReturnsSharedReferences()
        {
            SortedDictionary<ushort, byte[]> table = VocoderToneLookupTable.IMBEToneFrames;
            Assert.Same(table, VocoderToneLookupTable.IMBEToneFrames);
            Assert.Same(table[1000], table[1000]);
        }
    }
}
