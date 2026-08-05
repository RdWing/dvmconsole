// SPDX-License-Identifier: AGPL-3.0-only
/**
* Parent-owned headless compatibility baseline for the pinned fnecore
* submodule. These tests exercise existing public frame-codec APIs only
* (FneUtils bit/endian primitives, RtpHeader, P25Interleaver) and must stay
* deterministic: no sockets, no timers, no wall-clock assertions.
*/
using fnecore;
using fnecore.P25;
using Xunit;

namespace FneCore.Tests
{
    /// <summary>
    /// Asserts the parent-owned compatibility baseline for the pinned fnecore
    /// assembly: the assembly identity stays "fnecore" and it remains free of
    /// WPF/WinForms references so it can be consumed headlessly.
    /// </summary>
    public class AssemblyOwnershipTests
    {
        /// <summary>
        /// The canonical fnecore type must live in the fnecore assembly.
        /// </summary>
        [Fact]
        public void FneUtils_LivesInFnecoreAssembly()
        {
            Assert.Equal("fnecore", typeof(FneUtils).Assembly.GetName().Name);
        }

        /// <summary>
        /// The pinned fnecore library must stay headless: no WPF or WinForms
        /// framework references, so the same binary keeps serving the console
        /// and headless tooling.
        /// </summary>
        [Fact]
        public void FnecoreAssembly_HasNoWpfOrWinFormsReferences()
        {
            string[] refs = typeof(FneUtils).Assembly.GetReferencedAssemblies()
                .Select(a => a.Name)
                .ToArray();

            Assert.DoesNotContain("PresentationFramework", refs);
            Assert.DoesNotContain("WindowsBase", refs);
            Assert.DoesNotContain("System.Windows.Forms", refs);
        }
    }

    /// <summary>
    /// FneUtils bit-level and endian primitives used by every frame codec.
    /// </summary>
    public class FneUtilsTests
    {
        /// <summary>
        /// Memset must fill exactly the requested length, and a partial fill
        /// must leave the trailing bytes untouched.
        /// </summary>
        [Fact]
        public void Memset_FillsRequestedLength()
        {
            byte[] full = new byte[64];
            FneUtils.Memset(full, 0xA5, full.Length);
            Assert.All(full, b => Assert.Equal(0xA5, b));

            byte[] partial = new byte[64];
            FneUtils.Memset(partial, 0x5A, 16);
            for (int i = 0; i < 16; i++)
                Assert.Equal(0x5A, partial[i]);
            for (int i = 16; i < partial.Length; i++)
                Assert.Equal(0x00, partial[i]);
        }

        /// <summary>
        /// WriteBit/ReadBit round trip across byte boundaries on byte[].
        /// </summary>
        [Fact]
        public void WriteBitReadBit_RoundTripByteArray()
        {
            byte[] buf = new byte[4];
            uint[] set = { 0U, 7U, 8U, 15U, 16U, 23U, 31U };

            foreach (uint bit in set)
                FneUtils.WriteBit(ref buf, bit, true);

            Assert.Equal(0x81, buf[0]); // bits 0 and 7
            Assert.Equal(0x81, buf[1]); // bits 8 and 15
            Assert.Equal(0x81, buf[2]); // bits 16 and 23
            Assert.Equal(0x01, buf[3]); // bit 31

            for (uint i = 0; i < 32; i++)
                Assert.Equal(set.Contains(i), FneUtils.ReadBit(buf, i));

            // clearing a set bit must round trip back to false
            FneUtils.WriteBit(ref buf, 0U, false);
            Assert.False(FneUtils.ReadBit(buf, 0U));
            Assert.True(FneUtils.ReadBit(buf, 7U));
        }

        /// <summary>
        /// WriteBit/ReadBit round trip across byte boundaries on Span&lt;byte&gt;.
        /// </summary>
        [Fact]
        public void WriteBitReadBit_RoundTripSpan()
        {
            byte[] backing = new byte[4];
            Span<byte> span = backing;

            FneUtils.WriteBit(ref span, 0U, true);
            FneUtils.WriteBit(ref span, 15U, true);
            FneUtils.WriteBit(ref span, 31U, true);

            Assert.True(FneUtils.ReadBit(span, 0U));
            Assert.True(FneUtils.ReadBit(span, 15U));
            Assert.True(FneUtils.ReadBit(span, 31U));
            Assert.False(FneUtils.ReadBit(span, 1U));
            Assert.False(FneUtils.ReadBit(span, 16U));
            Assert.Equal(0x80, backing[0]);
            Assert.Equal(0x01, backing[1]);
            Assert.Equal(0x01, backing[3]);
        }

        /// <summary>
        /// ByteToBitsBE/BitsToByteBE must be exact inverses, honoring offset.
        /// </summary>
        [Fact]
        public void ByteBitsBE_RoundTrip()
        {
            byte value = 0xA5;
            bool[] bits = new bool[10];
            FneUtils.ByteToBitsBE(value, ref bits, 2);

            Assert.True(bits[2]);   // 0x80
            Assert.False(bits[3]);  // 0x40
            Assert.True(bits[4]);   // 0x20
            Assert.False(bits[5]);  // 0x10
            Assert.False(bits[6]);  // 0x08
            Assert.True(bits[7]);   // 0x04
            Assert.False(bits[8]);  // 0x02
            Assert.True(bits[9]);   // 0x01

            byte roundTrip = 0;
            FneUtils.BitsToByteBE(bits, 2, ref roundTrip);
            Assert.Equal(value, roundTrip);
        }

        /// <summary>
        /// ByteToBitsLE/BitsToByteLE must be exact inverses (LSB first).
        /// </summary>
        [Fact]
        public void ByteBitsLE_RoundTrip()
        {
            byte value = 0xA5;
            bool[] bits = new bool[8];
            FneUtils.ByteToBitsLE(value, ref bits, 0);

            Assert.True(bits[0]);   // 0x01
            Assert.False(bits[1]);  // 0x02
            Assert.True(bits[2]);   // 0x04
            Assert.False(bits[3]);  // 0x08
            Assert.False(bits[4]);  // 0x10
            Assert.True(bits[5]);   // 0x20
            Assert.False(bits[6]);  // 0x40
            Assert.True(bits[7]);   // 0x80

            byte roundTrip = 0;
            FneUtils.BitsToByteLE(bits, 0, ref roundTrip);
            Assert.Equal(value, roundTrip);
        }

        /// <summary>
        /// Big-endian WriteBytes/ToUInt* must round trip and lay bytes down MSB
        /// first, matching the RTP/fragment wire format.
        /// </summary>
        [Fact]
        public void EndianWriteRead_RoundTrip()
        {
            byte[] buf = new byte[32];
            FneUtils.Memset(buf, 0x00, buf.Length);

            ushort u16 = 0x1234;
            uint u32 = 0x89ABCDEFU;
            ulong u64 = 0x0123456789ABCDEFUL;

            FneUtils.WriteBytes(u16, ref buf, 0);
            FneUtils.WriteBytes(u32, ref buf, 2);
            FneUtils.WriteBytes(u64, ref buf, 6);

            // explicit MSB-first byte layout
            Assert.Equal(0x12, buf[0]);
            Assert.Equal(0x34, buf[1]);
            Assert.Equal(0x89, buf[2]);
            Assert.Equal(0xAB, buf[3]);
            Assert.Equal(0xCD, buf[4]);
            Assert.Equal(0xEF, buf[5]);
            Assert.Equal(0x01, buf[6]);
            Assert.Equal(0x23, buf[7]);
            Assert.Equal(0xEF, buf[13]);

            Assert.Equal(u16, FneUtils.ToUInt16(buf, 0));
            Assert.Equal(u32, FneUtils.ToUInt32(buf, 2));
            Assert.Equal(u64, FneUtils.ToUInt64(buf, 6));

            // 3-byte field used by frame headers
            uint u24 = 0x123456U;
            FneUtils.Write3Bytes(u24, ref buf, 14);
            Assert.Equal(0x12, buf[14]);
            Assert.Equal(0x34, buf[15]);
            Assert.Equal(0x56, buf[16]);
            Assert.Equal(u24, FneUtils.Bytes3ToUInt32(buf, 14));
        }

        /// <summary>
        /// DoReverseEndian must byte-swap on the host (little-endian) and be an
        /// involution for every width.
        /// </summary>
        [Fact]
        public void DoReverseEndian_ByteSwapsAndRoundTrips()
        {
            Assert.Equal((ushort)0x3412, FneUtils.DoReverseEndian((ushort)0x1234));
            Assert.Equal(0xEFCDAB89U, FneUtils.DoReverseEndian(0x89ABCDEFU));
            Assert.Equal(0xEFCDAB8967452301UL, FneUtils.DoReverseEndian(0x0123456789ABCDEFUL));
            Assert.Equal(0x78563412, FneUtils.DoReverseEndian(0x12345678));

            Assert.Equal((ushort)0x1234, FneUtils.DoReverseEndian(FneUtils.DoReverseEndian((ushort)0x1234)));
            Assert.Equal(0x89ABCDEFU, FneUtils.DoReverseEndian(FneUtils.DoReverseEndian(0x89ABCDEFU)));
        }
    }

    /// <summary>
    /// RtpHeader encode/decode round trips against the 12-byte RTP fixed
    /// header, plus the deterministic timestamp increment path.
    /// </summary>
    public class RtpHeaderTests
    {
        /// <summary>
        /// Encode must write the configured fields into the 12-byte header and
        /// Decode must recover them, including the SSRC and a fresh timestamp.
        /// </summary>
        [Fact]
        public void EncodeDecode_PreservesFields()
        {
            RtpHeader.ResetStartTime();

            var header = new RtpHeader
            {
                Extension = true,
                Marker = true,
                PayloadType = 0x5A,
                Sequence = 0xBEEF,
                SSRC = 0xDEADBEEFU
            };

            byte[] data = new byte[12];
            header.Encode(ref data);

            // wire layout: version 2 + extension flag + marker + payload type
            Assert.Equal(0x80, data[0] & 0xC0);      // version = 2
            Assert.NotEqual(0, data[0] & 0x10);      // extension flag set
            Assert.Equal(0, data[0] & 0x20);         // padding flag clear
            Assert.Equal(0, data[0] & 0x0F);         // CSRC count 0
            Assert.NotEqual(0, data[1] & 0x80);      // marker set
            Assert.Equal(0x5A, data[1] & 0x7F);      // payload type
            Assert.Equal(0xBE, data[2]);             // sequence MSB
            Assert.Equal(0xEF, data[3]);             // sequence LSB
            Assert.Equal(0xDE, data[8]);             // SSRC MSB
            Assert.Equal(0xEF, data[11]);            // SSRC LSB

            var decoded = new RtpHeader();
            Assert.True(decoded.Decode(data));

            Assert.Equal(header.Version, decoded.Version);
            Assert.Equal(header.Extension, decoded.Extension);
            Assert.Equal(header.Marker, decoded.Marker);
            Assert.Equal(header.PayloadType, decoded.PayloadType);
            Assert.Equal(header.Sequence, decoded.Sequence);
            Assert.Equal(header.SSRC, decoded.SSRC);
            // timestamp is clock-derived; only require it to be valid and
            // to match what Encode produced
            Assert.NotEqual(Constants.InvalidTS, decoded.Timestamp);
            Assert.Equal(header.Timestamp, decoded.Timestamp);
        }

        /// <summary>
        /// After a reset, consecutive Encode calls advance the timestamp by the
        /// fixed 8000/133 tick per packet, independent of the wall clock.
        /// </summary>
        [Fact]
        public void Encode_TimestampAdvancesByFixedTick()
        {
            RtpHeader.ResetStartTime();

            var header = new RtpHeader { Sequence = 1 };
            byte[] data1 = new byte[12];
            byte[] data2 = new byte[12];

            header.Encode(ref data1);
            uint first = header.Timestamp;
            header.Encode(ref data2);
            uint second = header.Timestamp;

            Assert.NotEqual(Constants.InvalidTS, first);
            Assert.Equal(first + (Constants.RtpGenericClockRate / 133), second);
        }

        /// <summary>
        /// Decode must reject null input and any header whose version bits are
        /// not 2, and accept a minimal valid header.
        /// </summary>
        [Fact]
        public void Decode_RejectsNullAndInvalidVersion()
        {
            var header = new RtpHeader();

            Assert.False(header.Decode(null));

            byte[] badVersion = new byte[12];
            FneUtils.Memset(badVersion, 0x00, badVersion.Length); // version 0
            Assert.False(header.Decode(badVersion));

            byte[] valid = new byte[12];
            valid[0] = 0x80; // version 2
            Assert.True(header.Decode(valid));
            Assert.Equal((byte)2, header.Version);
        }
    }

    /// <summary>
    /// P25Interleaver bit interleave/deinterleave round trip over a
    /// deterministic bit range, including the status-symbol skip positions.
    /// </summary>
    public class P25InterleaverTests
    {
        /// <summary>
        /// Encode then Decode over the same [start, stop) range must recover
        /// every interleaved bit, for both a zero start and a mid-frame start.
        /// </summary>
        [Fact]
        public void EncodeDecode_RoundTripRange()
        {
            (uint start, uint stop)[] ranges = { (0U, 196U), (196U, 400U) };

            foreach ((uint start, uint stop) in ranges)
            {
                byte[] input = new byte[64];
                for (int i = 0; i < input.Length; i++)
                    input[i] = (byte)((i * 37) & 0xFF);

                byte[] interleaved = new byte[64];
                uint written = P25Interleaver.Encode(input, ref interleaved, start, stop);
                Assert.True(written > 0);

                byte[] deinterleaved = new byte[64];
                uint read = P25Interleaver.Decode(interleaved, ref deinterleaved, start, stop);

                Assert.Equal(written, read);
                for (uint i = 0; i < written; i++)
                    Assert.Equal(FneUtils.ReadBit(input, i), FneUtils.ReadBit(deinterleaved, i));
            }
        }
    }
}
