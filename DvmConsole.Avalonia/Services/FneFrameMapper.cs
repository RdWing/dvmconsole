// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;
using fnecore;
using fnecore.DMR;
using fnecore.P25;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Translates raw fnecore receive events into the talkgroup audio
    /// router's inputs: DMR voice frames become 27-byte AMBE units (with
    /// the WPF nibble fix), P25 LDU1/LDU2 record messages become
    /// 225-byte LDUs, and terminators are classified so the pipeline can
    /// shed them. Talkgroup keys use
    /// <see cref="ResourceIdentity.Build(string, string)"/> with a
    /// <c>|slot:N</c> suffix for DMR (WPF statusKey parity
    /// MainWindow.DMR.cs:284).
    /// </summary>
    public static class FneFrameMapper
    {
        /// <summary>
        /// The size of a complete DMR AMBE frame: three 9-byte codewords.
        /// </summary>
        public const int DmrAmbeLengthBytes = 27;

        /// <summary>
        /// The size of a complete P25 LDU: nine 11-byte IMBE codewords
        /// plus the link-control/encryption bytes at the WPF decode
        /// offsets.
        /// </summary>
        public const int P25LduLengthBytes = 225;

        /// <summary>
        /// P25 record offsets inside the raw 200-byte message, after the
        /// 24-byte header: the first record starts at message[24] and the
        /// signature of record <c>r</c> sits at message[24 + offset[r]].
        /// </summary>
        private static readonly int[] P25RecordOffsets = { 0, 22, 36, 53, 70, 87, 104, 121, 138 };

        /// <summary>
        /// WPF decode offsets inside the assembled 225-byte LDU
        /// (MainWindow.P25.cs:600-718 parity).
        /// </summary>
        private static readonly int[] P25LduOffsets = { 0, 25, 50, 75, 100, 125, 150, 175, 200 };

        /// <summary>
        /// Per-record payload lengths, in WPF order.
        /// </summary>
        private static readonly int[] P25RecordLengths = { 22, 14, 17, 17, 17, 17, 17, 17, 16 };

        /// <summary>
        /// DFSI record signatures of an LDU1: 0x62-0x6A.
        /// </summary>
        private static readonly byte[] P25Ldu1Signatures =
        {
            P25DFSI.P25_DFSI_LDU1_VOICE1, P25DFSI.P25_DFSI_LDU1_VOICE2,
            P25DFSI.P25_DFSI_LDU1_VOICE3, P25DFSI.P25_DFSI_LDU1_VOICE4,
            P25DFSI.P25_DFSI_LDU1_VOICE5, P25DFSI.P25_DFSI_LDU1_VOICE6,
            P25DFSI.P25_DFSI_LDU1_VOICE7, P25DFSI.P25_DFSI_LDU1_VOICE8,
            P25DFSI.P25_DFSI_LDU1_VOICE9,
        };

        /// <summary>
        /// DFSI record signatures of an LDU2: 0x6B-0x73.
        /// </summary>
        private static readonly byte[] P25Ldu2Signatures =
        {
            P25DFSI.P25_DFSI_LDU2_VOICE10, P25DFSI.P25_DFSI_LDU2_VOICE11,
            P25DFSI.P25_DFSI_LDU2_VOICE12, P25DFSI.P25_DFSI_LDU2_VOICE13,
            P25DFSI.P25_DFSI_LDU2_VOICE14, P25DFSI.P25_DFSI_LDU2_VOICE15,
            P25DFSI.P25_DFSI_LDU2_VOICE16, P25DFSI.P25_DFSI_LDU2_VOICE17,
            P25DFSI.P25_DFSI_LDU2_VOICE18,
        };

        /// <summary>
        /// Extracts the 27 AMBE bytes from a DMR voice frame, or
        /// classifies a DATA_SYNC terminator. WPF parity
        /// MainWindow.DMR.cs:437-445: only VOICE_SYNC and VOICE frames
        /// produce audio; ambe[0..13] = message[0..13], ambe[13] low
        /// nibble from message[19], ambe[14..26] = message[20..32].
        /// </summary>
        /// <param name="e">The raw DMR receive event.</param>
        /// <param name="ambe">The 27-byte AMBE frame, or null for a terminator.</param>
        /// <param name="terminator">True when the frame is a TERMINATOR_WITH_LC.</param>
        /// <returns>True when the frame is audio or a terminator; false for other control frames.</returns>
        public static bool TryExtractDmr(DMRDataReceivedEvent e, out byte[]? ambe, out bool terminator)
        {
            ambe = null;
            terminator = false;

            if (e is null || e.Data is null || e.Data.Length < 33)
            {
                return false;
            }

            if (e.FrameType == FrameType.VOICE_SYNC || e.FrameType == FrameType.VOICE)
            {
                // WPF parity: 27 AMBE bytes with the nibble fix
                // (ambe[13] high nibble from message[13], low nibble
                // from message[19]).
                ambe = new byte[DmrAmbeLengthBytes];
                Buffer.BlockCopy(e.Data, 0, ambe, 0, 14);
                ambe[13] = (byte)((ambe[13] & 0xF0) | (e.Data[19] & 0x0F));
                Buffer.BlockCopy(e.Data, 20, ambe, 14, 13);
                return true;
            }

            if (e.FrameType == FrameType.DATA_SYNC && e.DataType == DMRDataType.TERMINATOR_WITH_LC)
            {
                terminator = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reconstructs the 225-byte LDU from the P25 record message, or
        /// classifies a TDU/TDULC terminator. WPF parity
        /// MainWindow.P25.cs:600-718: the record signatures are checked
        /// at the message offsets 24 + {0,22,36,53,70,87,104,121,138}
        /// and the records are BlockCopied into the LDU at the WPF
        /// decode offsets with the WPF per-record lengths.
        /// </summary>
        /// <param name="e">The raw P25 receive event.</param>
        /// <param name="ldu">The 225-byte LDU, or null for a terminator.</param>
        /// <param name="terminator">True when the frame is a TDU/TDULC.</param>
        /// <returns>True when the frame is audio or a terminator; false for other units or mismatched signatures.</returns>
        public static bool TryExtractP25(P25DataReceivedEvent e, out byte[]? ldu, out bool terminator)
        {
            ldu = null;
            terminator = false;

            if (e is null || e.Data is null)
            {
                return false;
            }

            switch (e.DUID)
            {
                case P25DUID.TDU:
                case P25DUID.TDULC:
                    terminator = true;
                    return true;

                case P25DUID.LDU1:
                case P25DUID.LDU2:
                    break;

                default:
                    return false;
            }

            // The records live after the 24-byte header; the last record
            // ends at 24 + 138 + 16.
            if (e.Data.Length < 24 + P25RecordOffsets[8] + P25RecordLengths[8])
            {
                return false;
            }

            byte[] signatures = e.DUID == P25DUID.LDU1 ? P25Ldu1Signatures : P25Ldu2Signatures;
            for (var r = 0; r < 9; r++)
            {
                if (e.Data[24 + P25RecordOffsets[r]] != signatures[r])
                {
                    return false;
                }
            }

            ldu = new byte[P25LduLengthBytes];
            for (var r = 0; r < 9; r++)
            {
                Buffer.BlockCopy(e.Data, 24 + P25RecordOffsets[r], ldu, P25LduOffsets[r], P25RecordLengths[r]);
            }

            return true;
        }

        /// <summary>
        /// Builds the DMR talkgroup router key for a received frame:
        /// <see cref="ResourceIdentity.Build(string, string)"/> of the
        /// system name and destination id, suffixed with the DMR slot
        /// (WPF statusKey parity MainWindow.DMR.cs:284).
        /// </summary>
        /// <param name="systemName">The FNE system name.</param>
        /// <param name="dstId">The destination talkgroup id.</param>
        /// <param name="slot">The DMR slot (0 or 1).</param>
        /// <returns>The stable router key.</returns>
        public static string BuildDmrTalkgroupKey(string systemName, uint dstId, byte slot)
            => ResourceIdentity.Build(systemName, dstId.ToString()) + $"|slot:{slot}";

        /// <summary>
        /// Builds the P25 talkgroup router key for a received frame:
        /// <see cref="ResourceIdentity.Build(string, string)"/> of the
        /// system name and destination id.
        /// </summary>
        /// <param name="systemName">The FNE system name.</param>
        /// <param name="dstId">The destination talkgroup id.</param>
        /// <returns>The stable router key.</returns>
        public static string BuildP25TalkgroupKey(string systemName, uint dstId)
            => ResourceIdentity.Build(systemName, dstId.ToString());
    }
}
