// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Pure-managed voice-frame math with WPF parity (MainWindow.DMR.cs /
    /// MainWindow.P25.cs): DMR 27-byte AMBE frames split into 3 x 9-byte
    /// codewords (FneSystemBase.DMR.cs: AMBE_BUF_LEN=9,
    /// DMR_AMBE_LENGTH_BYTES=27); P25 225-byte LDUs split into 9 x
    /// 11-byte IMBE codewords at the exact WPF offsets 10, 26, 55, 80,
    /// 105, 130, 155, 180, 204 (MainWindow.P25.cs:301-333); 320-byte PCM
    /// blocks convert to/from 160 little-endian 16-bit samples
    /// (MainWindow.DMR.cs:132-136); and 1600-byte capture blocks split
    /// into five 320-byte frames (AudioConverter.SplitToChunks parity).
    /// Wrong-length inputs produce an empty result — never throw.
    /// </summary>
    public static class VoiceFrameSplitter
    {
        /// <summary>Bytes in one complete DMR AMBE frame (3 codewords).</summary>
        private const int DmrFrameBytes = 27;

        /// <summary>Bytes in one DMR AMBE codeword.</summary>
        private const int DmrCodewordBytes = 9;

        /// <summary>Bytes in one complete P25 LDU (9 codewords).</summary>
        private const int P25LduBytes = 225;

        /// <summary>Bytes in one P25 IMBE codeword.</summary>
        private const int P25CodewordBytes = 11;

        /// <summary>
        /// Start offsets of the nine IMBE codewords within a 225-byte P25
        /// LDU (WPF parity, MainWindow.P25.cs:301-333 decode / :154-178
        /// encode). Shared with <see cref="TalkgroupAudioRouter"/> for
        /// transmit-side LDU assembly.
        /// </summary>
        internal static readonly int[] P25CodewordOffsets = { 10, 26, 55, 80, 105, 130, 155, 180, 204 };

        /// <summary>
        /// Splits a complete 27-byte DMR AMBE frame into its three 9-byte
        /// codewords, in order. A wrong-length frame yields an empty list.
        /// </summary>
        public static IReadOnlyList<byte[]> SplitDmrFrame(ReadOnlyMemory<byte> frame)
        {
            if (frame.Length != DmrFrameBytes)
            {
                return Array.Empty<byte[]>();
            }

            return new[]
            {
                frame.Slice(0, DmrCodewordBytes).ToArray(),
                frame.Slice(DmrCodewordBytes, DmrCodewordBytes).ToArray(),
                frame.Slice(DmrCodewordBytes * 2, DmrCodewordBytes).ToArray(),
            };
        }

        /// <summary>
        /// Splits a complete 225-byte P25 LDU into its nine 11-byte IMBE
        /// codewords at the exact WPF offsets (10, 26, 55, 80, 105, 130,
        /// 155, 180, 204). A wrong-length LDU yields an empty list.
        /// </summary>
        public static IReadOnlyList<byte[]> SplitP25Ldu(ReadOnlyMemory<byte> ldu)
        {
            if (ldu.Length != P25LduBytes)
            {
                return Array.Empty<byte[]>();
            }

            var codewords = new byte[P25CodewordOffsets.Length][];
            for (var i = 0; i < P25CodewordOffsets.Length; i++)
            {
                codewords[i] = ldu.Slice(P25CodewordOffsets[i], P25CodewordBytes).ToArray();
            }

            return codewords;
        }

        /// <summary>
        /// Converts a 320-byte PCM frame into its 160 little-endian
        /// 16-bit samples. A wrong-length frame yields an empty array.
        /// </summary>
        public static short[] BytesToSamples(ReadOnlyMemory<byte> pcm)
        {
            if (pcm.Length != AudioPcm.FrameBytes)
            {
                return Array.Empty<short>();
            }

            var samples = new short[AudioPcm.FrameBytes / 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Span.Slice(i * 2, 2));
            }

            return samples;
        }

        /// <summary>
        /// Converts 160 16-bit samples into their 320-byte little-endian
        /// PCM frame. A wrong-length sample array yields an empty array.
        /// </summary>
        public static byte[] SamplesToBytes(ReadOnlyMemory<short> samples)
        {
            if (samples.Length != AudioPcm.FrameBytes / 2)
            {
                return Array.Empty<byte>();
            }

            var pcm = new byte[AudioPcm.FrameBytes];
            for (var i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples.Span[i]);
            }

            return pcm;
        }

        /// <summary>
        /// Splits a 1600-byte capture block into its five 320-byte PCM
        /// frames (AudioConverter.SplitToChunks parity: a 1600-byte
        /// buffer yields 5 x 320-byte chunks). A wrong-length block
        /// yields an empty list.
        /// </summary>
        public static IReadOnlyList<byte[]> SplitBlock(ReadOnlyMemory<byte> block)
        {
            if (block.Length != AudioPcm.BlockBytes)
            {
                return Array.Empty<byte[]>();
            }

            var chunks = new byte[AudioPcm.BlockBytes / AudioPcm.FrameBytes][];
            for (var i = 0; i < chunks.Length; i++)
            {
                chunks[i] = block.Slice(i * AudioPcm.FrameBytes, AudioPcm.FrameBytes).ToArray();
            }

            return chunks;
        }
    }
}
