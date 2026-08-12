// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Gate 5.2 RED contracts for portable WAVE inspection. Inspection must
    /// validate the file without creating a CoreAudio stream or throwing
    /// user-facing file/format failures.
    /// </summary>
    public sealed class WaveFileInspectorTests
    {
        [Fact]
        public void ValidPcmWave_AllowsExtraChunksAndOddPadding()
        {
            using var file = new TempFile(BuildWave(
                Chunk("JUNK", new byte[] { 9, 8, 7 }),
                Chunk("fmt ", PcmFormat()),
                Chunk("LIST", new byte[] { 4, 3, 2, 1 }),
                Chunk("data", new byte[] { 1, 2, 3, 4 })));

            AudioWaveInspectionResult result = new WaveFileInspector().Inspect(file.FilePath);

            Assert.True(result.IsValid);
            Assert.Null(result.ErrorMessage);
        }

        [Fact]
        public void DataBeforeFormat_IsTypedInvalidResult()
        {
            using var file = new TempFile(BuildWave(
                Chunk("data", new byte[] { 1, 2 }),
                Chunk("fmt ", PcmFormat())));

            AudioWaveInspectionResult result = new WaveFileInspector().Inspect(file.FilePath);

            Assert.False(result.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void TruncatedChunk_IsTypedInvalidResult()
        {
            using var file = new TempFile(BuildWave(
                DeclaredChunk("fmt ", 16, new byte[] { 1, 0, 1, 0 })));

            AudioWaveInspectionResult result = new WaveFileInspector().Inspect(file.FilePath);

            Assert.False(result.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void UnsupportedFormat_IsTypedInvalidResult()
        {
            byte[] format = PcmFormat();
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(0, 2), 3);
            using var file = new TempFile(BuildWave(
                Chunk("fmt ", format),
                Chunk("data", new byte[] { 1, 2 })));

            AudioWaveInspectionResult result = new WaveFileInspector().Inspect(file.FilePath);

            Assert.False(result.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void WrongSampleFormat_IsTypedInvalidResult()
        {
            byte[] format = PcmFormat();
            BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4, 4), 16000);
            using var file = new TempFile(BuildWave(
                Chunk("fmt ", format),
                Chunk("data", new byte[] { 1, 2 })));

            AudioWaveInspectionResult result = new WaveFileInspector().Inspect(file.FilePath);

            Assert.False(result.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void MissingOddChunkPadding_IsTypedInvalidResult()
        {
            using var file = new TempFile(BuildWave(
                DeclaredChunk("JUNK", 3, new byte[] { 9, 8, 7 }),
                Chunk("fmt ", PcmFormat()),
                Chunk("data", new byte[] { 1, 2 })));

            AudioWaveInspectionResult result = new WaveFileInspector().Inspect(file.FilePath);

            Assert.False(result.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void MissingFile_IsTypedInvalidResult()
        {
            string path = Path.Combine(Path.GetTempPath(), "dvmconsole-missing-" + Guid.NewGuid().ToString("N") + ".wav");

            AudioWaveInspectionResult result = new WaveFileInspector().Inspect(path);

            Assert.False(result.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void BlankPath_IsTypedInvalidResult()
        {
            AudioWaveInspectionResult result = new WaveFileInspector().Inspect(" ");

            Assert.False(result.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        private static byte[] PcmFormat()
        {
            byte[] format = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(0, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4, 4), 8000);
            BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(8, 4), 16000);
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12, 2), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14, 2), 16);
            return format;
        }

        private static byte[] BuildWave(params byte[][] chunks)
        {
            var bytes = new List<byte>();
            bytes.AddRange(Encoding.ASCII.GetBytes("RIFF"));
            bytes.AddRange(new byte[4]);
            bytes.AddRange(Encoding.ASCII.GetBytes("WAVE"));
            foreach (byte[] chunk in chunks)
                bytes.AddRange(chunk);

            byte[] result = bytes.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)(result.Length - 8)));
            return result;
        }

        private static byte[] Chunk(string id, byte[] payload)
            => DeclaredChunk(id, checked((uint)payload.Length), payload)
                .Concat(payload.Length % 2 == 1 ? new byte[] { 0 } : Array.Empty<byte>())
                .ToArray();

        private static byte[] DeclaredChunk(string id, uint declaredSize, byte[] payload)
        {
            byte[] header = new byte[8];
            Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), declaredSize);
            return header.Concat(payload).ToArray();
        }

        private sealed class TempFile : IDisposable
        {
            public TempFile(byte[] bytes)
            {
                FilePath = Path.Combine(
                    Path.GetTempPath(),
                    "dvmconsole-wave-inspector-" + Guid.NewGuid().ToString("N") + ".wav");
                File.WriteAllBytes(FilePath, bytes);
            }

            public string FilePath { get; }

            public void Dispose()
            {
                try
                {
                    File.Delete(FilePath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }
    }
}
