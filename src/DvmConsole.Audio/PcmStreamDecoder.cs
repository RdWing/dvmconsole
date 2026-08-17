namespace DvmConsole.Audio;

// Chooses a portable decoder from the beginning of a stream. WAV remains
// handled by the in-box PCM reader; MPEG Layer I/II/III and Ogg Opus audio are
// decoded by managed adapters. Other formats can opt into an explicitly
// configured FFmpeg process through `DVM_FFMPEG`.
public static class PcmStreamDecoder
{
    public static async Task<IAudioPcmStreamReader> OpenAsync(
        Stream source,
        CancellationToken cancellationToken = default)
        => await OpenAsync(
            source,
            Environment.GetEnvironmentVariable("DVM_FFMPEG"),
            cancellationToken).ConfigureAwait(false);

    public static async Task<IAudioPcmStreamReader> OpenAsync(
        Stream source,
        string? ffmpegExecutable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The audio source must be readable.", nameof(source));

        byte[] prefix = new byte[4];
        await ReadExactlyAsync(source, prefix, cancellationToken).ConfigureAwait(false);
        var replay = new PrefixStream(prefix, source);
        try
        {
            if (prefix.AsSpan().SequenceEqual("RIFF"u8))
                return await WavPcmStreamReader.OpenAsync(replay, cancellationToken).ConfigureAwait(false);
            if (prefix.AsSpan().SequenceEqual("OggS"u8))
                return await OpusOggPcmStreamReader.OpenAsync(replay, cancellationToken).ConfigureAwait(false);
            if (LooksLikeMpeg(prefix))
                return await MpegPcmStreamReader.OpenAsync(replay, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ffmpegExecutable))
            {
                return await FfmpegPcmStreamReader.OpenAsync(
                    replay,
                    ffmpegExecutable,
                    cancellationToken).ConfigureAwait(false);
            }

            throw new NotSupportedException(
                "Only PCM WAV, MPEG, and Ogg Opus audio streams are supported unless DVM_FFMPEG is configured.");
        }
        catch
        {
            await replay.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool LooksLikeMpeg(ReadOnlySpan<byte> prefix)
        => prefix[..3].SequenceEqual("ID3"u8) ||
           (prefix[0] == 0xFF && (prefix[1] & 0xE0) == 0xE0);

    private static async Task ReadExactlyAsync(
        Stream source,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = await source.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The audio stream ended before its format could be identified.");
            total += read;
        }
    }

    private sealed class PrefixStream(byte[] prefix, Stream inner) : Stream
    {
        private int prefixOffset;
        private long position;
        private bool disposed;

        public override bool CanRead => !disposed && inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.FromException(new NotSupportedException());

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int copied = CopyPrefix(buffer);
            if (copied == buffer.Length)
                return copied;
            int read = inner.Read(buffer[copied..]);
            position += read;
            return copied + read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int copied = CopyPrefix(buffer.Span);
            if (copied == buffer.Length)
                return copied;
            int read = await inner.ReadAsync(buffer[copied..], cancellationToken).ConfigureAwait(false);
            position += read;
            return copied + read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException(new NotSupportedException());
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new NotSupportedException());

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                await inner.DisposeAsync().ConfigureAwait(false);
            }

            GC.SuppressFinalize(this);
        }

        private int CopyPrefix(Span<byte> destination)
        {
            int count = Math.Min(destination.Length, prefix.Length - prefixOffset);
            if (count == 0)
                return 0;
            prefix.AsSpan(prefixOffset, count).CopyTo(destination);
            prefixOffset += count;
            position += count;
            return count;
        }
    }
}
