// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the fnecore frame-mapper slice (plan Task 10
* / vertical-slice gate item 3: one real FNE connection through the
* shell). The mapper translates raw fnecore receive events into the
* talkgroup audio router's inputs:
*
*   DvmConsole.Avalonia.Services.FneFrameMapper
*
* DMR (WPF parity MainWindow.DMR.cs:437-445): only VOICE_SYNC and
* VOICE frames produce audio; the 27 AMBE bytes are assembled with the
* nibble fix (ambe[0..13] = data[0..13], ambe[13] low nibble from
* data[19], ambe[14..26] = data[20..32]); DATA_SYNC +
* TERMINATOR_WITH_LC is classified as a terminator (dropped — the
* router's 2 s idle shed ends the pipeline). P25 (WPF parity
* MainWindow.P25.cs:600-718): LDU1 (records 0x62-0x6A) and LDU2
* (0x6B-0x73) reconstruct a 225-byte LDU via the WPF-exact record
* offset table; TDU/TDULC are terminators; mismatched record
* signatures are skipped. Talkgroup keys use
* ResourceIdentity.Build(systemName, dstId) with a "|slot:N" suffix
* for DMR (WPF statusKey parity MainWindow.DMR.cs:284).
*/
using System;
using System.Linq;
using DvmConsole.Avalonia.Services;
using fnecore;
using fnecore.DMR;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="FneFrameMapper"/>.
    /// </summary>
    public sealed class FneFrameMapperTests
    {
        /* ------------------------------------------------------------------
        ** DMR
        ** ---------------------------------------------------------------- */

        [Fact]
        public void TryExtractDmr_VoiceFrame_Assembles27AmbeBytes_WithNibbleFix()
        {
            // 55-byte message: data[0..13] -> ambe[0..13], ambe[13] low
            // nibble from data[19], data[20..32] -> ambe[14..26].
            var message = new byte[55];
            for (var i = 0; i < message.Length; i++)
            {
                message[i] = (byte)(0xA0 + i);
            }

            message[13] = 0x5F; // high nibble 0x5 kept, low nibble replaced
            message[19] = 0x03; // low nibble source

            var e = new DMRDataReceivedEvent(
                1, 1001, 31001, 0, CallType.GROUP, FrameType.VOICE, DMRDataType.VOICE_LC_HEADER,
                0, 0, 1, message);

            var ok = FneFrameMapper.TryExtractDmr(e, out var ambe, out var terminator);

            Assert.True(ok);
            Assert.False(terminator);
            Assert.Equal(27, ambe.Length);
            for (var i = 0; i < 13; i++)
            {
                Assert.Equal(message[i], ambe[i]);
            }

            Assert.Equal(0x53, ambe[13]); // (0x5F & 0xF0) | (0x03 & 0x0F)
            for (var i = 0; i < 13; i++)
            {
                Assert.Equal(message[20 + i], ambe[14 + i]);
            }
        }

        [Fact]
        public void TryExtractDmr_TerminatorWithLc_IsTerminator()
        {
            var e = new DMRDataReceivedEvent(
                1, 1001, 31001, 0, CallType.GROUP, FrameType.DATA_SYNC,
                DMRDataType.TERMINATOR_WITH_LC, 0, 0, 1, new byte[55]);

            var ok = FneFrameMapper.TryExtractDmr(e, out var ambe, out var terminator);

            Assert.True(ok);
            Assert.True(terminator);
            Assert.Null(ambe);
        }

        [Fact]
        public void TryExtractDmr_NonVoiceControlFrame_False()
        {
            var e = new DMRDataReceivedEvent(
                1, 1001, 31001, 0, CallType.GROUP, FrameType.DATA_SYNC,
                DMRDataType.VOICE_PI_HEADER, 0, 0, 1, new byte[55]);

            Assert.False(FneFrameMapper.TryExtractDmr(e, out _, out _));
        }

        [Fact]
        public void BuildDmrTalkgroupKey_SystemDstIdSlot_Suffixed()
        {
            // WPF statusKey parity (MainWindow.DMR.cs:284): Build(system,
            // dstId) + "|slot:N".
            var key = FneFrameMapper.BuildDmrTalkgroupKey("My System", 31001, 0);
            Assert.Equal("my system|31001|slot:0", key);
        }

        /* ------------------------------------------------------------------
        ** P25
        ** ---------------------------------------------------------------- */

        [Fact]
        public void TryExtractP25_Ldu1Records_Assembles225ByteLdu_AtWpfOffsets()
        {
            // Raw record message: 24-byte header (duid at [22], len at [23])
            // then the DFSI records; WPF slices records from offset 24 and
            // BlockCopies them into the 225-byte LDU at the pinned table
            // (MainWindow.P25.cs:600-649): record 0 -> ldu[0..21] (22B),
            // record 1 -> ldu[25..38] (14B), records 2-7 -> ldu[50..191]
            // (17B each), record 8 -> ldu[200..215] (16B).
            var message = new byte[200];
            message[22] = 0x00; // duid (LDU1 carried by the records)
            message[23] = 200;  // len

            byte[] sig = { 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A };
            int[] recordOffsets = { 0, 22, 36, 53, 70, 87, 104, 121, 138 };
            int[] lduOffsets = { 0, 25, 50, 75, 100, 125, 150, 175, 200 };
            int[] lengths = { 22, 14, 17, 17, 17, 17, 17, 17, 16 };
            for (var r = 0; r < 9; r++)
            {
                var start = 24 + recordOffsets[r];
                message[start] = sig[r];
                for (var i = 1; i < lengths[r]; i++)
                {
                    message[start + i] = (byte)(r * 32 + i);
                }
            }

            var e = new P25DataReceivedEvent(
                1, 1001, 31001, CallType.GROUP, P25DUID.LDU1, FrameType.VOICE, 0, 1, message);

            var ok = FneFrameMapper.TryExtractP25(e, out var ldu, out var terminator);

            Assert.True(ok);
            Assert.False(terminator);
            Assert.Equal(225, ldu.Length);
            // Record signatures and payloads land at the WPF decode offsets.
            for (var r = 0; r < 9; r++)
            {
                Assert.Equal(sig[r], ldu[lduOffsets[r]]);
                Assert.Equal((byte)(r * 32 + 1), ldu[lduOffsets[r] + 1]);
            }

            // The IMBE codeword regions sit at the decode offsets
            // (10, 26, 55, 80, 105, 130, 155, 180, 204) inside the LDU;
            // ldu[10] carries the 11th byte of record 0's payload (fill
            // value 0x0A at i=10).
            Assert.Equal((byte)0x0A, ldu[10]);
        }

        [Fact]
        public void TryExtractP25_Ldu2Records_Assembles225ByteLdu()
        {
            var message = new byte[200];
            message[22] = 0x00;
            message[23] = 200;
            byte[] sig = { 0x6B, 0x6C, 0x6D, 0x6E, 0x6F, 0x70, 0x71, 0x72, 0x73 };
            int[] recordOffsets = { 0, 22, 36, 53, 70, 87, 104, 121, 138 };
            int[] lduOffsets = { 0, 25, 50, 75, 100, 125, 150, 175, 200 };
            int[] lengths = { 22, 14, 17, 17, 17, 17, 17, 17, 16 };
            for (var r = 0; r < 9; r++)
            {
                var start = 24 + recordOffsets[r];
                message[start] = sig[r];
                for (var i = 1; i < lengths[r]; i++)
                {
                    message[start + i] = (byte)(0x80 + r * 32 + i);
                }
            }

            var e = new P25DataReceivedEvent(
                1, 1001, 31001, CallType.GROUP, P25DUID.LDU2, FrameType.VOICE, 0, 1, message);

            Assert.True(FneFrameMapper.TryExtractP25(e, out var ldu, out var terminator));
            Assert.False(terminator);
            Assert.Equal(225, ldu.Length);
            for (var r = 0; r < 9; r++)
            {
                Assert.Equal(sig[r], ldu[lduOffsets[r]]);
            }
        }

        [Fact]
        public void TryExtractP25_MismatchedSignatures_False()
        {
            var message = new byte[200];
            message[22] = 0x00;
            message[23] = 200;
            // Only the first signature matches; the rest are wrong.
            message[24] = 0x62;
            for (var i = 25; i < 200; i++)
            {
                message[i] = 0xFF;
            }

            var e = new P25DataReceivedEvent(
                1, 1001, 31001, CallType.GROUP, P25DUID.LDU1, FrameType.VOICE, 0, 1, message);

            Assert.False(FneFrameMapper.TryExtractP25(e, out _, out _));
        }

        [Fact]
        public void TryExtractP25_Tdu_Terminator()
        {
            var e = new P25DataReceivedEvent(
                1, 1001, 31001, CallType.GROUP, P25DUID.TDU, FrameType.TERMINATOR, 0, 1,
                new byte[200]);

            Assert.True(FneFrameMapper.TryExtractP25(e, out _, out var terminator));
            Assert.True(terminator);
        }

        [Fact]
        public void BuildP25TalkgroupKey_SystemDstId()
        {
            var key = FneFrameMapper.BuildP25TalkgroupKey("My System", 31001);
            Assert.Equal("my system|31001", key);
        }

        /* ------------------------------------------------------------------
        ** Surface
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Surface_IsPublicStaticSealed()
        {
            var type = typeof(FneFrameMapper);
            Assert.True(type.IsAbstract && type.IsSealed);
            Assert.NotNull(type.GetMethod("TryExtractDmr"));
            Assert.NotNull(type.GetMethod("TryExtractP25"));
            Assert.NotNull(type.GetMethod("BuildDmrTalkgroupKey"));
            Assert.NotNull(type.GetMethod("BuildP25TalkgroupKey"));
        }
    }
}
