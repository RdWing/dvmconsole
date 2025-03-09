/*
* WhackerLink - WhackerLinkConsoleV2
*
* This program is free software: you can redistribute it and/or modify
* it under the terms of the GNU General Public License as published by
* the Free Software Foundation, either version 3 of the License, or
* (at your option) any later version.
*
* This program is distributed in the hope that it will be useful,
* but WITHOUT ANY WARRANTY; without even the implied warranty of
* MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
* GNU General Public License for more details.
*
* You should have received a copy of the GNU General Public License
* along with this program.  If not, see <http://www.gnu.org/licenses/>.
* 
* Derrived from https://github.com/boatbod/op25/op25/gr-op25_repeater/lib/p25_crypt_algs.cc
* 
* Copyright (C) 2025 Caleb, K4PHP
* 
*/

namespace WhackerLinkConsoleV2
{
    /// <summary>
    /// P25 Crypto class
    /// </summary>
    public class P25Crypto
    {
        private ProtocolType protocol;
        private byte algId;
        private ushort keyId;
        private byte[] messageIndicator = new byte[9];
        private Dictionary<ushort, KeyInfo> keys = new Dictionary<ushort, KeyInfo>();
        private byte[] adpKeystream = new byte[469];
        private int adpPosition;

        /// <summary>
        /// Creates an instance of <see cref="P25Crypto"/>
        /// </summary>
        public P25Crypto()
        {
            this.protocol = ProtocolType.Unknown;
            this.algId = 0x80;
            this.keyId = 0;
            this.adpPosition = 0;
        }

        /// <summary>
        /// Clear keys
        /// </summary>
        public void Reset()
        {
            keys.Clear();
        }

        /// <summary>
        /// Add key to keys list
        /// </summary>
        /// <param name="keyid"></param>
        /// <param name="algid"></param>
        /// <param name="key"></param>
        public void AddKey(ushort keyid, byte algid, byte[] key)
        {
            if (keyid == 0 || algid == 0x80)
                return;

            keys[keyid] = new KeyInfo(algid, key);
        }

        /// <summary>
        /// Prepare P25 encryption meta data info
        /// </summary>
        /// <param name="algid"></param>
        /// <param name="keyid"></param>
        /// <param name="protocol"></param>
        /// <param name="MI"></param>
        /// <returns></returns>
        public bool Prepare(byte algid, ushort keyid, ProtocolType protocol, byte[] MI)
        {
            this.algId = algid;
            this.keyId = keyid;
            Array.Copy(MI, this.messageIndicator, Math.Min(MI.Length, this.messageIndicator.Length));

            if (!keys.ContainsKey(keyid))
            {
                return false;
            }

            if (algid == 0xAA)
            {
                this.adpPosition = 0;
                this.protocol = protocol;
                AdpKeystreamGen();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Process P25 frames for crypto
        /// </summary>
        /// <param name="PCW"></param>
        /// <param name="frameType"></param>
        /// <param name="voiceSubframe"></param>
        /// <returns></returns>
        public bool Process(byte[] PCW, FrameType frameType, int voiceSubframe)
        {
            if (!keys.ContainsKey(keyId))
                return false;

            if (algId == 0xAA)
                return AdpProcess(PCW, frameType, voiceSubframe);

            return false;
        }

        /// <summary>
        /// Cycles the P25 LFSR (Linear Feedback Shift Register) based on the given polynomial.
        /// </summary>
        /// <param name="MI">The message indicator array to be processed.</param>
        public static void CycleP25Lfsr(byte[] MI)
        {
            if (MI == null || MI.Length < 9)
                throw new ArgumentException("MI must be at least 9 bytes long.");

            ulong lfsr = 0;

            // Load the first 8 bytes into the LFSR
            for (int i = 0; i < 8; i++)
            {
                lfsr = (lfsr << 8) | MI[i];
            }

            // Perform 64-bit LFSR cycling using the polynomial:
            // C(x) = x^64 + x^62 + x^46 + x^38 + x^27 + x^15 + 1
            for (int cnt = 0; cnt < 64; cnt++)
            {
                ulong bit = ((lfsr >> 63) ^ (lfsr >> 61) ^ (lfsr >> 45) ^ (lfsr >> 37) ^ (lfsr >> 26) ^ (lfsr >> 14)) & 0x1;
                lfsr = (lfsr << 1) | bit;
            }

            // Store the result back into MI
            for (int i = 7; i >= 0; i--)
            {
                MI[i] = (byte)(lfsr & 0xFF);
                lfsr >>= 8;
            }

            MI[8] = 0; // Last byte is always set to zero
        }

        /// <summary>
        /// Process RC4
        /// </summary>
        /// <param name="PCW"></param>
        /// <param name="frameType"></param>
        /// <param name="voiceSubframe"></param>
        /// <returns></returns>
        private bool AdpProcess(byte[] PCW, FrameType frameType, int voiceSubframe)
        {
            int offset = 256;

            switch (frameType)
            {
                case FrameType.LDU1: offset = 0; break;
                case FrameType.LDU2: offset = 101; break;
                case FrameType.V4_0: offset += 7 * voiceSubframe; break;
                case FrameType.V4_1: offset += 7 * (voiceSubframe + 4); break;
                case FrameType.V4_2: offset += 7 * (voiceSubframe + 8); break;
                case FrameType.V4_3: offset += 7 * (voiceSubframe + 12); break;
                case FrameType.V2: offset += 7 * (voiceSubframe + 16); break;
                default: return false;
            }

            if (protocol == ProtocolType.P25Phase1)
            {
                offset += (adpPosition * 11) + 267 + (adpPosition < 8 ? 0 : 2);
                adpPosition = (adpPosition + 1) % 9;
                for (int j = 0; j < 11; ++j)
                {
                    PCW[j] ^= adpKeystream[j + offset];
                }
            }
            else if (protocol == ProtocolType.P25Phase2)
            {
                for (int j = 0; j < 7; ++j)
                {
                    PCW[j] ^= adpKeystream[j + offset];
                }
                PCW[6] &= 0x80;
            }

            return true;
        }

        /// <summary>
        /// Create RC4 key stream
        /// </summary>
        private void AdpKeystreamGen()
        {
            byte[] adpKey = new byte[13];
            byte[] S = new byte[256];
            byte[] K = new byte[256];

            if (!keys.ContainsKey(keyId))
                return;

            byte[] keyData = keys[keyId].Key;

            int keySize = keyData.Length;
            int padding = Math.Max(5 - keySize, 0);
            int i, j = 0, k; 

            for (i = 0; i < padding; i++)
                adpKey[i] = 0; 

            for (; i < 5; i++)
                adpKey[i] = keySize > 0 ? keyData[i - padding] : (byte)0; 

            for (i = 5; i < 13; ++i)
            {
                adpKey[i] = messageIndicator[i - 5];
            }

            for (i = 0; i < 256; ++i)
            {
                K[i] = adpKey[i % 13];
                S[i] = (byte)i;
            }

            for (i = 0; i < 256; ++i) 
            {
                j = (j + S[i] + K[i]) & 0xFF;
                Swap(S, i, j);
            }

            i = j = 0;
            for (k = 0; k < 469; ++k)
            {
                i = (i + 1) & 0xFF;
                j = (j + S[i]) & 0xFF;
                Swap(S, i, j);
                adpKeystream[k] = S[(S[i] + S[j]) & 0xFF];
            }
        }

        /// <summary>
        /// Preform a swap
        /// </summary>
        /// <param name="S"></param>
        /// <param name="i"></param>
        /// <param name="j"></param>
        private void Swap(byte[] S, int i, int j)
        {
            byte temp = S[i];
            S[i] = S[j];
            S[j] = temp;
        }

        /// <summary>
        /// P25 protocol type
        /// </summary>
        public enum ProtocolType
        {
            Unknown = 0,
            P25Phase1,
            P25Phase2
        }

        /// <summary>
        /// P25 frame type
        /// </summary>
        public enum FrameType
        {
            Unknown = 0,
            LDU1,
            LDU2,
            V2,
            V4_0,
            V4_1,
            V4_2,
            V4_3
        }

        /// <summary>
        /// Key info object
        /// </summary>
        private class KeyInfo
        {
            public byte AlgId { get; }
            public byte[] Key { get; }

            public KeyInfo(byte algid, byte[] key)
            {
                AlgId = algid;
                Key = key;
            }
        }
    }
}
