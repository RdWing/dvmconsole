// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the voice-frame math slice (plan Task 11
* coverage minimum: DMR/P25 audio chunk conversion):
*
*   DvmConsole.Platform.Audio.VoiceFrameSplitter
*
* Pure-managed frame math with WPF parity (MainWindow.DMR.cs /
* MainWindow.P25.cs): DMR 27-byte AMBE frames split into 3 x 9-byte
* codewords (FneSystemBase.DMR.cs: AMBE_BUF_LEN=9, DMR_AMBE_LENGTH_BYTES=27);
* P25 225-byte LDUs split into 9 x 11-byte IMBE codewords at the exact
* WPF offsets 10, 26, 55, 80, 105, 130, 155, 180, 204
* (MainWindow.P25.cs:301-333); 320-byte PCM blocks convert to/from
* 160 little-endian 16-bit samples (MainWindow.DMR.cs:132-136).
* Wrong-length inputs produce an empty result — never throw.
*/
using System;
using System.Linq;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="VoiceFrameSplitter"/>.
    /// </summary>
    public sealed class VoiceFrameSplitterTests
    {
        [Fact]
        public void SplitDmrFrame_27Bytes_Three9ByteCodewords()
        {
            var frame = new byte[27];
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (byte)(i + 1);
            }

            var codewords = VoiceFrameSplitter.SplitDmrFrame(frame);

            Assert.Equal(3, codewords.Count);
            Assert.All(codewords, c => Assert.Equal(9, c.Length));
            Assert.Equal(frame.Take(9), codewords[0]);
            Assert.Equal(frame.Skip(9).Take(9), codewords[1]);
            Assert.Equal(frame.Skip(18).Take(9), codewords[2]);
        }

        [Fact]
        public void SplitDmrFrame_WrongLength_Empty_NoThrow()
        {
            Assert.Empty(VoiceFrameSplitter.SplitDmrFrame(new byte[9]));
            Assert.Empty(VoiceFrameSplitter.SplitDmrFrame(new byte[26]));
            Assert.Empty(VoiceFrameSplitter.SplitDmrFrame(new byte[28]));
        }

        [Fact]
        public void SplitP25Ldu_225Bytes_Nine11ByteCodewords_AtExactOffsets()
        {
            var ldu = new byte[225];
            for (var i = 0; i < ldu.Length; i++)
            {
                ldu[i] = (byte)(i + 1);
            }

            var codewords = VoiceFrameSplitter.SplitP25Ldu(ldu);

            Assert.Equal(9, codewords.Count);
            Assert.All(codewords, c => Assert.Equal(11, c.Length));

            // WPF offsets (MainWindow.P25.cs:301-333): the nine IMBE
            // codewords start at 10, 26, 55, 80, 105, 130, 155, 180, 204.
            int[] offsets = { 10, 26, 55, 80, 105, 130, 155, 180, 204 };
            for (var i = 0; i < 9; i++)
            {
                Assert.Equal(ldu.Skip(offsets[i]).Take(11), codewords[i]);
            }
        }

        [Fact]
        public void SplitP25Ldu_WrongLength_Empty_NoThrow()
        {
            Assert.Empty(VoiceFrameSplitter.SplitP25Ldu(new byte[224]));
            Assert.Empty(VoiceFrameSplitter.SplitP25Ldu(new byte[226]));
        }

        [Fact]
        public void BytesToSamples_320Bytes_160LittleEndianShorts()
        {
            var pcm = new byte[320];
            for (var i = 0; i < pcm.Length; i += 2)
            {
                // Little-endian (WPF parity MainWindow.DMR.cs:134:
                // (pcm[i+1] << 8) + pcm[i]): bytes {0x02, 0x01} -> 0x0102 = 258.
                pcm[i] = 0x02;
                pcm[i + 1] = 0x01;
            }

            var samples = VoiceFrameSplitter.BytesToSamples(pcm);

            Assert.Equal(160, samples.Length);
            Assert.All(samples, s => Assert.Equal((short)0x0102, s));
        }

        [Fact]
        public void SamplesToBytes_160Shorts_320LittleEndianBytes()
        {
            var samples = new short[160];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)(0x0201 + i);
            }

            var pcm = VoiceFrameSplitter.SamplesToBytes(samples);

            Assert.Equal(320, pcm.Length);
            for (var i = 0; i < samples.Length; i++)
            {
                Assert.Equal((byte)(samples[i] & 0xFF), pcm[i * 2]);
                Assert.Equal((byte)((samples[i] >> 8) & 0xFF), pcm[i * 2 + 1]);
            }
        }

        [Fact]
        public void RoundTrip_SamplesToBytesToSamples_PreservesValues()
        {
            var samples = new short[160];
            var rng = new Random(42);
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)rng.Next(short.MinValue, short.MaxValue + 1);
            }

            var roundTrip = VoiceFrameSplitter.BytesToSamples(
                VoiceFrameSplitter.SamplesToBytes(samples));

            Assert.Equal(samples, roundTrip);
        }

        [Fact]
        public void SamplesToBytes_WrongLength_Empty_NoThrow()
        {
            Assert.Empty(VoiceFrameSplitter.SamplesToBytes(new short[159]));
            Assert.Empty(VoiceFrameSplitter.SamplesToBytes(new short[161]));
        }

        [Fact]
        public void Surface_IsPublicStaticSealed()
        {
            var type = typeof(VoiceFrameSplitter);
            Assert.True(type.IsAbstract && type.IsSealed);
            Assert.NotNull(type.GetMethod("SplitDmrFrame"));
            Assert.NotNull(type.GetMethod("SplitP25Ldu"));
            Assert.NotNull(type.GetMethod("BytesToSamples"));
            Assert.NotNull(type.GetMethod("SamplesToBytes"));
        }
    }
}
