// SPDX-License-Identifier: AGPL-3.0-only
/**
* Minimal RIFF/WAVE writer for Core TAR recordings.
*/
using System.Buffers.Binary;
using System.Text;

namespace dvmconsole
{
    internal static class TarWavWriter
    {
        public static void Write(string filePath, byte[] pcmBytes, int sampleRate, short bitsPerSample, short channelCount)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A WAV file path is required.", nameof(filePath));
            if (pcmBytes == null)
                throw new ArgumentNullException(nameof(pcmBytes));
            if (bitsPerSample <= 0 || bitsPerSample % 8 != 0)
                throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channelCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(channelCount));

            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            int blockAlign = channelCount * (bitsPerSample / 8);
            int byteRate = sampleRate * blockAlign;
            byte[] header = new byte[44];
            Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), checked(36 + pcmBytes.Length));
            Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
            Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), channelCount);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), byteRate);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), checked((short)blockAlign));
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), bitsPerSample);
            Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), pcmBytes.Length);

            using FileStream stream = File.Create(filePath);
            stream.Write(header);
            stream.Write(pcmBytes, 0, pcmBytes.Length);
        }
    }
}
