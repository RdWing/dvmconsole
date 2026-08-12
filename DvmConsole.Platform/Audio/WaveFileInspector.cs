// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Validates that a WAVE file is the PCM mono 8 kHz 16-bit format emitted by
    /// the TAR recorder. Inspection is purely structural: it never creates an
    /// audio stream and never throws user-facing file/format failures — every
    /// failure is reported as a typed <see cref="AudioWaveInspectionResult"/>.
    /// </summary>
    public sealed class WaveFileInspector : IAudioWaveFileInspector
    {
        private const ushort PcmFormatTag = 1;
        private const ushort ExpectedChannels = 1;
        private const uint ExpectedSampleRate = 8000;
        private const ushort ExpectedBitsPerSample = 16;
        private const int RiffHeaderSize = 12;
        private const int ChunkHeaderSize = 8;
        private const int MinimumFormatPayloadSize = 16;

        private static readonly byte[] RiffTag = Encoding.ASCII.GetBytes("RIFF");
        private static readonly byte[] WaveTag = Encoding.ASCII.GetBytes("WAVE");
        private static readonly byte[] FormatChunkId = Encoding.ASCII.GetBytes("fmt ");
        private static readonly byte[] DataChunkId = Encoding.ASCII.GetBytes("data");

        /// <inheritdoc/>
        public AudioWaveInspectionResult Inspect(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return AudioWaveInspectionResult.Invalid("Path is null or blank.");

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
            {
                return AudioWaveInspectionResult.Invalid("Cannot read the WAVE file: " + ex.Message);
            }

            return InspectBytes(bytes);
        }

        private static AudioWaveInspectionResult InspectBytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < RiffHeaderSize
                || !bytes.Slice(0, 4).SequenceEqual(RiffTag)
                || !bytes.Slice(8, 4).SequenceEqual(WaveTag))
            {
                return AudioWaveInspectionResult.Invalid("File is not a RIFF/WAVE file.");
            }

            uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
            long riffEnd = ChunkHeaderSize + riffSize;
            if (riffEnd > bytes.Length)
                return AudioWaveInspectionResult.Invalid("RIFF chunk is truncated.");

            bool sawFormat = false;
            bool sawData = false;
            int offset = RiffHeaderSize;
            while (offset < riffEnd)
            {
                if (riffEnd - offset < ChunkHeaderSize)
                    return AudioWaveInspectionResult.Invalid("Truncated chunk header.");

                ReadOnlySpan<byte> chunkId = bytes.Slice(offset, 4);
                uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
                long payloadStart = offset + ChunkHeaderSize;
                long payloadEnd = payloadStart + chunkSize;
                if (payloadEnd > riffEnd)
                    return AudioWaveInspectionResult.Invalid(
                        "Chunk '" + Encoding.ASCII.GetString(chunkId) + "' is truncated.");

                if (chunkId.SequenceEqual(FormatChunkId))
                {
                    if (!sawFormat)
                    {
                        AudioWaveInspectionResult formatResult = ValidateFormat(bytes.Slice((int)payloadStart, (int)chunkSize));
                        if (!formatResult.IsValid)
                            return formatResult;
                        sawFormat = true;
                    }
                }
                else if (chunkId.SequenceEqual(DataChunkId))
                {
                    if (!sawFormat)
                        return AudioWaveInspectionResult.Invalid("Data chunk appears before the fmt chunk.");
                    sawData = true;
                }

                // RIFF pads odd-sized chunk payloads with a single byte.
                if (chunkSize % 2 == 1)
                {
                    if (payloadEnd >= riffEnd)
                        return AudioWaveInspectionResult.Invalid("Odd-sized chunk is missing its RIFF padding byte.");
                    offset = (int)payloadEnd + 1;
                }
                else
                {
                    offset = (int)payloadEnd;
                }
            }

            if (!sawFormat)
                return AudioWaveInspectionResult.Invalid("Missing fmt chunk.");
            if (!sawData)
                return AudioWaveInspectionResult.Invalid("Missing data chunk.");
            return AudioWaveInspectionResult.Valid();
        }

        private static AudioWaveInspectionResult ValidateFormat(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < MinimumFormatPayloadSize)
                return AudioWaveInspectionResult.Invalid("fmt chunk is too short.");

            ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            if (formatTag != PcmFormatTag)
                return AudioWaveInspectionResult.Invalid(
                    "Unsupported audio format " + formatTag + "; only PCM is supported.");

            ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2));
            if (channels != ExpectedChannels)
                return AudioWaveInspectionResult.Invalid(
                    "Unsupported channel count " + channels + "; only mono is supported.");

            uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4));
            if (sampleRate != ExpectedSampleRate)
                return AudioWaveInspectionResult.Invalid(
                    "Unsupported sample rate " + sampleRate + "; only 8000 Hz is supported.");

            ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(14));
            if (bitsPerSample != ExpectedBitsPerSample)
                return AudioWaveInspectionResult.Invalid(
                    "Unsupported bit depth " + bitsPerSample + "; only 16-bit is supported.");

            return AudioWaveInspectionResult.Valid();
        }
    }
}
