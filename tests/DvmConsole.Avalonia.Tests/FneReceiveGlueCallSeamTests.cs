// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the Call History slice (audit deleg_79328deb
* READY) — the additive FneReceiveGlue seam:
*
*   - public sealed record ReceivedCallMetadata(string SystemName,
*     uint SrcId, uint DstId, byte Slot, VoiceMode Mode, uint StreamId,
*     string Key, bool IsTerminator).
*   - public event Action<ReceivedCallMetadata>? CallFrameObserved;
*     raised for every CLASSIFIED frame (voice AND terminator, before
*     routing); control frames (unclassifiable) stay silent.
*   - The glue ctor and OnDmrFrame/OnP25Frame signatures are UNCHANGED
*     (the 477-test surface holds); routing/terminator-drop behavior is
*     untouched — these tests cover METADATA ONLY.
*/
using System;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using fnecore;
using fnecore.DMR;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for the call-history seam on
    /// <see cref="FneReceiveGlue"/>.
    /// </summary>
    public sealed class FneReceiveGlueCallSeamTests
    {
        /* ------------------------------------------------------------------
        ** Fixture helpers (WPF-parity frame construction, mirroring
        ** FnecoreTransportTests' existing DMR/P25 vectors)
        ** ---------------------------------------------------------------- */

        private static FneReceiveGlue MakeGlue(Action<string, ReadOnlyMemory<byte>, VoiceMode>? route = null)
            => new FneReceiveGlue(route ?? ((_, _, _) => { }));

        private static DMRDataReceivedEvent MakeDmrVoice(uint src, uint dst, byte slot, uint streamId)
        {
            var data = new byte[55];
            // bits @[15]: slot 0x80, group 0x40, frameType VOICE (1<<4).
            data[15] = (byte)(0x80 | 0x40 | (1 << 4));
            data[20] = 0x01;
            data[21] = 0x02;
            data[22] = 0x03;
            data[23] = 0x04;
            data[24] = 0x05;
            data[25] = 0x06;
            data[26] = 0x07;
            data[27] = 0x08;
            data[28] = 0x09;
            data[29] = 0x0A;
            data[30] = 0x0B;
            data[31] = 0x0C;
            data[32] = 0x0D;
            return new DMRDataReceivedEvent(
                1000001, src, dst, slot, CallType.GROUP, FrameType.VOICE,
                DMRDataType.VOICE_LC_HEADER, 0, 0, streamId, data);
        }

        private static DMRDataReceivedEvent MakeDmrTerminator(uint src, uint dst, byte slot, uint streamId)
        {
            // Mapper parity: DATA_SYNC + TERMINATOR_WITH_LC => terminator.
            var data = new byte[55];
            data[15] = (byte)(0x80 | 0x40 | ((int)FrameType.DATA_SYNC << 4));
            return new DMRDataReceivedEvent(
                1000001, src, dst, slot, CallType.GROUP, FrameType.DATA_SYNC,
                DMRDataType.TERMINATOR_WITH_LC, 0, 0, streamId, data);
        }

        private static P25DataReceivedEvent MakeP25Voice(uint src, uint dst, uint streamId)
        {
            // LDU1: 24-byte header; nine records with signatures 0x62-0x6A
            // at message offsets 24+{0,22,36,53,70,87,104,121,138}
            // (FneFrameMapper P25Ldu1Signatures / P25RecordOffsets parity).
            var data = new byte[225];
            var sig = new byte[] { 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A };
            var offsets = new int[] { 0, 22, 36, 53, 70, 87, 104, 121, 138 };
            for (var r = 0; r < 9; r++)
            {
                data[24 + offsets[r]] = sig[r];
            }
            data[22] = 0x62; // first record DUID (unused by the mapper)
            return new P25DataReceivedEvent(
                1000001, src, dst, CallType.GROUP, P25DUID.LDU1,
                FrameType.VOICE, 0, streamId, data);
        }

        /* ------------------------------------------------------------------
        ** Seam surface
        ** ---------------------------------------------------------------- */

        [Fact]
        public void ApiShape_RecordAndEvent()
        {
            var record = typeof(ReceivedCallMetadata);
            Assert.True(record.IsSealed);
            Assert.NotNull(record.GetConstructor(new[]
            {
                typeof(string), typeof(uint), typeof(uint), typeof(byte),
                typeof(VoiceMode), typeof(uint), typeof(string), typeof(bool),
            }));

            var glue = typeof(FneReceiveGlue);
            var evt = glue.GetEvent(nameof(FneReceiveGlue.CallFrameObserved));
            Assert.NotNull(evt);
            Assert.Equal(typeof(Action<ReceivedCallMetadata>), evt!.EventHandlerType);
            Assert.NotNull(evt.AddMethod);
            Assert.NotNull(evt.RemoveMethod);
        }

        /* ------------------------------------------------------------------
        ** DMR voice: metadata raised, routing untouched
        /* ---------------------------------------------------------------- */

        [Fact]
        public void DmrVoice_RaisesMetadata_BeforeRouting()
        {
            ReceivedCallMetadata? seen = null;
            var glue = MakeGlue();
            glue.CallFrameObserved += m => seen = m;

            glue.OnDmrFrame("System 1", MakeDmrVoice(111, 222, 1, 7));

            Assert.NotNull(seen);
            Assert.Equal("System 1", seen!.SystemName);
            Assert.Equal(111u, seen.SrcId);
            Assert.Equal(222u, seen.DstId);
            Assert.Equal((byte)1, seen.Slot);
            Assert.Equal(VoiceMode.Dmr, seen.Mode);
            Assert.Equal(7u, seen.StreamId);
            Assert.Equal("system 1|222|slot:1", seen.Key);
            Assert.False(seen.IsTerminator);
        }

        [Fact]
        public void ThrowingObserver_DoesNotSuppressVoiceRouting()
        {
            var routed = 0;
            var glue = MakeGlue((_, _, _) => routed++);
            glue.CallFrameObserved += _ => throw new InvalidOperationException("history observer failed");

            glue.OnDmrFrame("System 1", MakeDmrVoice(111, 222, 1, 7));

            Assert.Equal(1, routed);
        }

        [Fact]
        public void DmrTerminator_RaisesMetadata_IsTerminator_ButNotRouted()
        {
            ReceivedCallMetadata? seen = null;
            int routed = 0;
            var glue = MakeGlue((_, _, _) => routed++);
            glue.CallFrameObserved += m => seen = m;

            glue.OnDmrFrame("System 1", MakeDmrTerminator(111, 222, 1, 7));

            Assert.NotNull(seen);
            Assert.True(seen!.IsTerminator);
            Assert.Equal(0, routed); // terminator still dropped for routing
        }

        [Fact]
        public void DmrControlFrame_Silent()
        {
            ReceivedCallMetadata? seen = null;
            var glue = MakeGlue();
            glue.CallFrameObserved += m => seen = m;

            // DATA_SYNC frame: not voice, not terminator — unclassifiable.
            var data = new byte[55];
            data[15] = (byte)(0x80 | 0x40 | ((int)FrameType.DATA_SYNC << 4));
            glue.OnDmrFrame("System 1", new DMRDataReceivedEvent(
                1000001, 111, 222, 1, CallType.GROUP, FrameType.DATA_SYNC,
                DMRDataType.DATA_HEADER, 0, 0, 7, data));

            Assert.Null(seen);
        }

        /* ------------------------------------------------------------------
        ** P25 voice
        /* ---------------------------------------------------------------- */

        [Fact]
        public void P25Voice_RaisesMetadata()
        {
            ReceivedCallMetadata? seen = null;
            var glue = MakeGlue();
            glue.CallFrameObserved += m => seen = m;

            glue.OnP25Frame("System 1", MakeP25Voice(111, 222, 7));

            Assert.NotNull(seen);
            Assert.Equal(VoiceMode.P25, seen!.Mode);
            Assert.Equal(111u, seen.SrcId);
            Assert.Equal(222u, seen.DstId);
            Assert.Equal(7u, seen.StreamId);
            Assert.Equal("system 1|222", seen.Key);
            Assert.False(seen.IsTerminator);
        }

        /* ------------------------------------------------------------------
        ** Observer detach on dispose
        ** (receive-glue composition review deleg_22cc7617 finding 3;
        ** audit deleg_1e79ef4e READY)
        /* ---------------------------------------------------------------- */

        [Fact]
        public void Dispose_DetachesCallFrameObservers_AndRouting()
        {
            int observed = 0;
            int routed = 0;
            var glue = MakeGlue((_, _, _) => routed++);
            glue.CallFrameObserved += _ => observed++;

            glue.OnDmrFrame("System 1", MakeDmrVoice(111, 222, 1, 7));
            Assert.Equal(1, observed);
            Assert.Equal(1, routed);

            glue.Dispose();
            glue.Dispose(); // idempotent

            // A late adapter frame after the shell closed must neither
            // reach the call-history observer nor the router delegate.
            glue.OnDmrFrame("System 1", MakeDmrVoice(111, 222, 1, 7));
            glue.OnP25Frame("System 1", MakeP25Voice(111, 222, 7));
            Assert.Equal(1, observed);
            Assert.Equal(1, routed);
        }
    }
}
