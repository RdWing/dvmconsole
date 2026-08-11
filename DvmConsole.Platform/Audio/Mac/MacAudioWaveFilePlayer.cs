// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Platform.Audio.Mac
{
    /// <summary>
    /// Reads the standard PCM WAVE files emitted by TarRecorder and feeds only
    /// their data chunk into the existing CoreAudio PCM playback path.
    /// </summary>
    internal sealed class MacAudioWaveFilePlayer : IAudioWaveFilePlayer
    {
        private readonly MacAudioFilePlayer pcmPlayer;

        internal MacAudioWaveFilePlayer(MacAudioStreamFactory factory)
        {
            pcmPlayer = new MacAudioFilePlayer(factory ?? throw new ArgumentNullException(nameof(factory)));
        }

        public async Task<AudioPlaybackResult> PlayWavAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, "A WAVE file path is required.");

            try
            {
                await using WavPcmStream stream = WavPcmStream.Open(filePath);
                return await pcmPlayer.PlayPcmStreamAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Cancelled, null);
            }
            catch (AudioDeviceException exception)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, exception.Message);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, exception.Message);
            }
        }

        public Task StopAsync() => pcmPlayer.StopAsync();

        private sealed class WavPcmStream : Stream
        {
            private readonly FileStream stream;
            private readonly long dataLength;
            private long remaining;

            private WavPcmStream(FileStream stream, long dataLength)
            {
                this.stream = stream;
                this.dataLength = dataLength;
                remaining = dataLength;
            }

            public static WavPcmStream Open(string filePath)
            {
                var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    AudioPcm.BlockBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                try
                {
                    long dataLength = ReadHeader(stream);
                    return new WavPcmStream(stream, dataLength);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            private static long ReadHeader(FileStream stream)
            {
                Span<byte> header = stackalloc byte[12];
                ReadExactly(stream, header);
                if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
                    throw new InvalidDataException("The TAR recording is not a RIFF WAVE file.");

                bool validFormat = false;
                while (stream.Position + 8 <= stream.Length)
                {
                    byte[] chunkHeader = new byte[8];
                    ReadExactly(stream, chunkHeader);
                    string chunkId = Encoding.ASCII.GetString(chunkHeader[..4]);
                    uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
                    long chunkEnd = stream.Position + chunkSize;
                    if (chunkEnd > stream.Length)
                        throw new InvalidDataException("The TAR recording contains a truncated WAVE chunk.");

                    if (chunkId == "fmt ")
                    {
                        if (chunkSize < 16)
                            throw new InvalidDataException("The TAR recording has an invalid WAVE format chunk.");
                        byte[] format = new byte[16];
                        ReadExactly(stream, format);
                        ushort audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(format[..2]);
                        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..4]);
                        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..8]);
                        ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format[14..16]);
                        validFormat = audioFormat == 1
                            && channels == AudioPcm.Console.Channels
                            && sampleRate == AudioPcm.Console.SampleRate
                            && bitsPerSample == AudioPcm.Console.BitsPerSample;
                    }
                    else if (chunkId == "data")
                    {
                        if (!validFormat)
                            throw new InvalidDataException("The TAR recording has an unsupported WAVE format.");
                        return chunkSize;
                    }

                    stream.Position = chunkEnd + (chunkSize & 1);
                }

                throw new InvalidDataException("The TAR recording has no WAVE data chunk.");
            }

            private static void ReadExactly(Stream stream, Span<byte> buffer)
            {
                while (!buffer.IsEmpty)
                {
                    int count = stream.Read(buffer);
                    if (count == 0)
                        throw new EndOfStreamException("The TAR recording has an incomplete WAVE header.");
                    buffer = buffer[count..];
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (remaining == 0)
                    return 0;
                int requested = (int)Math.Min(count, remaining);
                int read = stream.Read(buffer, offset, requested);
                remaining -= read;
                return read;
            }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (remaining == 0)
                    return ValueTask.FromResult(0);
                int requested = (int)Math.Min(buffer.Length, remaining);
                return ReadChunkAsync(buffer[..requested], cancellationToken);
            }

            private async ValueTask<int> ReadChunkAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                remaining -= read;
                return read;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    stream.Dispose();
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                GC.SuppressFinalize(this);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => dataLength;
            public override long Position
            {
                get => dataLength - remaining;
                set => throw new NotSupportedException();
            }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
