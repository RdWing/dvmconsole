// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Gate 0.2 RED contracts for WAVE file-open errors and the private parser
    /// that bounds playback to the validated PCM data chunk.
    /// </summary>
    public sealed class MacAudioWaveFilePlayerGateTests
    {
        [Fact]
        public async Task UnreadableWave_ReturnsTypedFailureWithoutThrowing()
        {
            using var file = new TempFile(BuildWave(
                Chunk("fmt ", PcmFormat()),
                Chunk("data", new byte[] { 1, 2 })));
            if (OperatingSystem.IsWindows())
                return;

            try
            {
                File.SetUnixFileMode(file.FilePath, UnixFileMode.None);
                var result = await CreatePlayer().PlayWavAsync(file.FilePath, CancellationToken.None);

                Assert.Equal(AudioPlaybackOutcome.Failed, result.Outcome);
                Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
            }
            finally
            {
                File.SetUnixFileMode(file.FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        [Fact]
        public async Task Parser_ReadsOnlyDataChunkAndSkipsOddChunkPadding()
        {
            using var file = new TempFile(BuildWave(
                Chunk("JUNK", new byte[] { 9, 8, 7 }),
                Chunk("fmt ", PcmFormat()),
                Chunk("data", new byte[] { 1, 2, 3, 4, 5 }))
                .Concat(new byte[] { 0x7F, 0x7E })
                .ToArray());

            await using Stream stream = OpenParser(file.FilePath);
            Assert.Equal(5, stream.Length);

            byte[] buffer = new byte[16];
            int count = await stream.ReadAsync(buffer, CancellationToken.None);

            Assert.Equal(5, count);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer[..count]);
            Assert.Equal(0, await stream.ReadAsync(buffer, CancellationToken.None));
        }

        [Fact]
        public void Parser_RejectsDataBeforeFormat()
        {
            using var file = new TempFile(BuildWave(
                Chunk("data", new byte[] { 1, 2 }),
                Chunk("fmt ", PcmFormat())));

            Assert.IsType<InvalidDataException>(OpenParserException(file.FilePath));
        }

        [Fact]
        public void Parser_RejectsTruncatedChunk()
        {
            using var file = new TempFile(BuildWave(
                DeclaredChunk("fmt ", 16, new byte[] { 1, 0, 1, 0 })));

            Assert.IsType<InvalidDataException>(OpenParserException(file.FilePath));
        }

        [Fact]
        public void Parser_RejectsUnsupportedFormat()
        {
            byte[] format = PcmFormat();
            BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(0, 2), 3);
            using var file = new TempFile(BuildWave(
                Chunk("fmt ", format),
                Chunk("data", new byte[] { 1, 2 })));

            Assert.IsType<InvalidDataException>(OpenParserException(file.FilePath));
        }

        private static IAudioWaveFilePlayer CreatePlayer()
        {
            Type playerType = typeof(MacAudioStreamFactory).Assembly.GetType(
                "DvmConsole.Platform.Audio.Mac.MacAudioWaveFilePlayer",
                throwOnError: true)!;
            var factory = (MacAudioStreamFactory)RuntimeHelpers.GetUninitializedObject(
                typeof(MacAudioStreamFactory));
            return (IAudioWaveFilePlayer)Activator.CreateInstance(
                playerType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object?[] { factory },
                culture: null)!;
        }

        private static Stream OpenParser(string filePath)
        {
            Type parserType = typeof(MacAudioStreamFactory).Assembly.GetType(
                "DvmConsole.Platform.Audio.Mac.MacAudioWaveFilePlayer+WavPcmStream",
                throwOnError: true)!;
            MethodInfo open = parserType.GetMethod(
                "Open",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            return Assert.IsAssignableFrom<Stream>(open.Invoke(null, new object[] { filePath }));
        }

        private static Exception OpenParserException(string filePath)
        {
            try
            {
                _ = OpenParser(filePath);
                throw new InvalidOperationException("The WAVE parser accepted an invalid file.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                return exception.InnerException;
            }
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
        {
            return DeclaredChunk(id, checked((uint)payload.Length), payload)
                .Concat(payload.Length % 2 == 1 ? new byte[] { 0 } : Array.Empty<byte>())
                .ToArray();
        }

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
                FilePath = Path.Combine(Path.GetTempPath(), "dvmconsole-wave-gate-" + Guid.NewGuid().ToString("N") + ".wav");
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
