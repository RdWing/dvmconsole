// Based on OP25 p25_crypt_algs.cpp

namespace WhackerLinkConsoleV2
{
    public class P25Crypto
    {
        private int debug;
        private int msgqId;
        private ProtocolType prType;
        private byte algId;
        private ushort keyId;
        private byte[] messageIndicator = new byte[9];
        private Dictionary<ushort, KeyInfo> keys = new Dictionary<ushort, KeyInfo>();
        private byte[] adpKeystream = new byte[469];
        private int adpPosition;

        public P25Crypto(int debug = 0, int msgqId = 0)
        {
            this.debug = debug;
            this.msgqId = msgqId;
            this.prType = ProtocolType.Unknown;
            this.algId = 0x80;
            this.keyId = 0;
            this.adpPosition = 0;
        }

        public void Reset()
        {
            keys.Clear();
        }

        public void AddKey(ushort keyid, byte algid, byte[] key)
        {
            if (keyid == 0 || algid == 0x80)
                return;

            keys[keyid] = new KeyInfo(algid, key);
        }

        public bool Prepare(byte algid, ushort keyid, ProtocolType prType, byte[] MI)
        {
            this.algId = algid;
            this.keyId = keyid;
            Array.Copy(MI, this.messageIndicator, Math.Min(MI.Length, this.messageIndicator.Length));

            if (!keys.ContainsKey(keyid))
            {
                if (debug >= 10)
                    Console.Error.WriteLine($"P25Crypto::Prepare: KeyID [0x{keyid:X}] not found");

                return false;
            }

            if (debug >= 10)
                Console.WriteLine($"P25Crypto::Prepare: KeyID [0x{keyid:X}] found");

            if (algid == 0xAA) // ADP RC4
            {
                this.adpPosition = 0;
                this.prType = prType;
                AdpKeystreamGen();
                return true;
            }

            return false;
        }

        public bool Process(byte[] PCW, FrameType frameType, int voiceSubframe)
        {
            if (!keys.ContainsKey(keyId))
                return false;

            if (algId == 0xAA) // ADP RC4
                return AdpProcess(PCW, frameType, voiceSubframe);

            return false;
        }

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

            if (prType == ProtocolType.P25Phase1)
            {
                // FDMA
                offset += (adpPosition * 11) + 267 + (adpPosition < 8 ? 0 : 2);
                adpPosition = (adpPosition + 1) % 9;
                for (int j = 0; j < 11; ++j)
                {
                    PCW[j] ^= adpKeystream[j + offset];
                }
            }
            else if (prType == ProtocolType.P25Phase2)
            {
                // TDMA
                for (int j = 0; j < 7; ++j)
                {
                    PCW[j] ^= adpKeystream[j + offset];
                }
                PCW[6] &= 0x80; // Mask everything except MSB of the final codeword
            }

            return true;
        }

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

            // Append MI bytes
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
                Swap(ref S[i], ref S[j]);
            }

            i = j = 0;
            for (k = 0; k < 469; ++k)
            {
                i = (i + 1) & 0xFF;
                j = (j + S[i]) & 0xFF;
                Swap(ref S[i], ref S[j]);
                adpKeystream[k] = S[(S[i] + S[j]) & 0xFF];
            }
        }

        private void Swap(ref byte a, ref byte b)
        {
            byte temp = a;
            a = b;
            b = temp;
        }

        public enum ProtocolType
        {
            Unknown = 0,
            P25Phase1,
            P25Phase2
        }

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
