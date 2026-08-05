// SPDX-License-Identifier: AGPL-3.0-only
/**
* Parent-owned headless compatibility baseline for the pinned fnecore
* submodule: PacketBuffer fragment codec. Tests build wire fragments by hand
* against the documented layout (10-byte header, big-endian lengths) so the
* baseline stays independent of the library's own write helpers.
*/
using fnecore;
using Xunit;

namespace FneCore.Tests
{
    /// <summary>
    /// PacketBuffer fragment encode/decode and input validation against the
    /// pinned fnecore implementation.
    /// </summary>
    public class PacketBufferTests
    {
        /// <summary>
        /// A single uncompressed fragment (length 3, compressed length 3,
        /// block 0 of 0, three payload bytes) must decode to the exact
        /// payload.
        /// </summary>
        [Fact]
        public void SingleFragmentUncompressed_DecodesPayload()
        {
            var buffer = new PacketBuffer(false, "test");
            byte[] frag = new byte[13];

            // header: uncompressed length = 3 (big-endian)
            frag[0] = 0x00; frag[1] = 0x00; frag[2] = 0x00; frag[3] = 0x03;
            // header: compressed length = 3 (big-endian)
            frag[4] = 0x00; frag[5] = 0x00; frag[6] = 0x00; frag[7] = 0x03;
            frag[8] = 0x00; // block id
            frag[9] = 0x00; // total blocks - 1
            frag[10] = 0xAA;
            frag[11] = 0xBB;
            frag[12] = 0xCC;

            bool ok = buffer.Decode(frag, out byte[] message, out uint outLength);

            Assert.True(ok);
            Assert.Equal(3U, outLength);
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, message);
            // a successful decode must not leave state behind
            Assert.Empty(buffer.Fragments);
        }

        /// <summary>
        /// Fragments shorter than the 10-byte header must be rejected without
        /// touching buffer state.
        /// </summary>
        [Fact]
        public void TruncatedHeader_Rejected()
        {
            var buffer = new PacketBuffer(false, "test");

            Assert.False(buffer.Decode(Array.Empty<byte>(), out _, out uint lenEmpty));
            Assert.Equal(0U, lenEmpty);

            byte[] shortFrag = new byte[9];
            Assert.False(buffer.Decode(shortFrag, out _, out uint lenShort));
            Assert.Equal(0U, lenShort);
        }

        /// <summary>
        /// Fragment metadata claiming more than the 8 MB cap must be rejected
        /// as a DOS guard, for both the uncompressed and compressed length
        /// fields.
        /// </summary>
        [Fact]
        public void OversizedMetadata_Rejected()
        {
            var buffer = new PacketBuffer(false, "test");

            byte[] bigUncompressed = new byte[14];
            bigUncompressed[0] = 0xFF; bigUncompressed[1] = 0xFF;
            bigUncompressed[2] = 0xFF; bigUncompressed[3] = 0xFF; // 4 GiB - 1
            bigUncompressed[8] = 0x00; bigUncompressed[9] = 0x00;
            Assert.False(buffer.Decode(bigUncompressed, out _, out uint lenU));
            Assert.Equal(0U, lenU);

            byte[] bigCompressed = new byte[14];
            bigCompressed[3] = 0x03;
            bigCompressed[4] = 0xFF; bigCompressed[5] = 0xFF;
            bigCompressed[6] = 0xFF; bigCompressed[7] = 0xFF; // 4 GiB - 1
            bigCompressed[8] = 0x00; bigCompressed[9] = 0x00;
            Assert.False(buffer.Decode(bigCompressed, out _, out uint lenC));
            Assert.Equal(0U, lenC);
        }

        /// <summary>
        /// A first fragment declaring zero sizes must be rejected (an empty
        /// packet is never valid).
        /// </summary>
        [Fact]
        public void ZeroSizedMetadata_Rejected()
        {
            var buffer = new PacketBuffer(false, "test");

            byte[] frag = new byte[13]; // all zero header: length 0, compressed 0
            frag[8] = 0x00;
            frag[9] = 0x00;

            Assert.False(buffer.Decode(frag, out _, out uint len));
            Assert.Equal(0U, len);
        }

        /// <summary>
        /// Compressed encode must produce a wire fragment that decodes back to
        /// the original payload through the zlib path (deterministic for fixed
        /// input).
        /// </summary>
        [Fact]
        public void CompressedRoundTrip_EncodeDecode()
        {
            var buffer = new PacketBuffer(true, "test");

            byte[] payload = new byte[64];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)((i * 13 + 7) & 0xFF);

            buffer.Encode(payload);
            var fragment = Assert.Single(buffer.Fragments.Values);

            bool ok = buffer.Decode(fragment.Data, out byte[] message, out uint outLength);

            Assert.True(ok);
            Assert.Equal((uint)payload.Length, outLength);
            Assert.Equal(payload, message);
            Assert.Empty(buffer.Fragments);
        }

        /// <summary>
        /// Encode must refuse empty input.
        /// </summary>
        [Fact]
        public void Encode_EmptyInput_Throws()
        {
            var buffer = new PacketBuffer(false, "test");
            Assert.Throws<ArgumentException>(() => buffer.Encode(Array.Empty<byte>()));
        }
    }
}
