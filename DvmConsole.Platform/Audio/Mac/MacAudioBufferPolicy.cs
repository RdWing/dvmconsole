// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;

namespace DvmConsole.Platform.Audio.Mac
{
    /// <summary>
    /// Bounded FIFO byte buffer for macOS capture audio. Capacity is derived
    /// from the PCM byte rate and a maximum buffered duration; storage is a
    /// single ring allocation made once at construction, so steady-state
    /// operation performs no unbounded allocations. Writes are atomic, reads
    /// preserve write order, and backlog shedding drops the oldest bytes first.
    /// </summary>
    public sealed class MacAudioBufferPolicy
    {
        private readonly byte[] _buffer;
        private readonly int _capacity;
        private readonly long _bytesPerSecond;
        private int _head;
        private int _count;

        /// <summary>
        /// Creates a bounded buffer for <paramref name="format"/> that can hold
        /// at most <paramref name="maxDuration"/> of audio.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="maxDuration"/> is not positive or the
        /// format byte rate is not positive.
        /// </exception>
        public MacAudioBufferPolicy(PcmFormat format, TimeSpan maxDuration)
        {
            if (maxDuration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDuration), maxDuration, "The maximum buffered duration must be positive.");
            }

            if (format.BytesPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(format), format.BytesPerSecond,
                    "The PCM format must have a positive byte rate.");
            }

            var capacity = format.BytesPerSecond * maxDuration.TotalSeconds;
            _capacity = capacity > int.MaxValue ? int.MaxValue : (int)capacity;
            _bytesPerSecond = format.BytesPerSecond;
            _buffer = new byte[_capacity];
        }

        /// <summary>Total storage in bytes (floor of byte rate times max duration).</summary>
        public int CapacityBytes => _capacity;

        /// <summary>Number of bytes currently buffered.</summary>
        public int BufferedBytes => _count;

        /// <summary>Duration of audio currently buffered, at the format byte rate.</summary>
        public TimeSpan BufferedDuration =>
            TimeSpan.FromTicks(_count * TimeSpan.TicksPerSecond / _bytesPerSecond);

        /// <summary>
        /// Appends <paramref name="data"/> when it fits in the remaining space.
        /// An overflow write is rejected as a whole: nothing is appended and
        /// the buffer state is unchanged.
        /// </summary>
        /// <returns>True when the full payload was appended.</returns>
        public bool TryWrite(ReadOnlyMemory<byte> data)
        {
            if (data.Length == 0)
            {
                return true;
            }

            if (data.Length > _capacity - _count)
            {
                return false;
            }

            var tail = (_head + _count) % _capacity;
            var span = data.Span;
            var firstChunk = Math.Min(span.Length, _capacity - tail);
            span.Slice(0, firstChunk).CopyTo(_buffer.AsSpan(tail, firstChunk));
            if (firstChunk < span.Length)
            {
                span.Slice(firstChunk).CopyTo(_buffer.AsSpan(0, span.Length - firstChunk));
            }

            _count += span.Length;
            return true;
        }

        /// <summary>
        /// Copies up to <paramref name="destination"/>.Length buffered bytes to
        /// <paramref name="destination"/> in FIFO order and removes them from
        /// the buffer.
        /// </summary>
        /// <returns>The number of bytes read, or zero when the buffer is empty.</returns>
        public int Read(Span<byte> destination)
        {
            if (destination.Length == 0 || _count == 0)
            {
                return 0;
            }

            var toRead = Math.Min(destination.Length, _count);
            var firstChunk = Math.Min(toRead, _capacity - _head);
            _buffer.AsSpan(_head, firstChunk).CopyTo(destination.Slice(0, firstChunk));
            if (firstChunk < toRead)
            {
                _buffer.AsSpan(0, toRead - firstChunk).CopyTo(destination.Slice(firstChunk));
            }

            _head = (_head + toRead) % _capacity;
            _count -= toRead;
            return toRead;
        }

        /// <summary>
        /// When the buffered duration exceeds <paramref name="threshold"/>, sheds
        /// the oldest bytes until the buffered duration fits, preserving the
        /// newest audio.
        /// </summary>
        /// <param name="threshold">Maximum acceptable buffered duration.</param>
        /// <returns>True when bytes were removed.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="threshold"/> is not positive.</exception>
        public bool ShedBacklogIfOver(TimeSpan threshold)
        {
            if (threshold <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(threshold), threshold, "The shed threshold must be positive.");
            }

            if (_count == 0)
            {
                return false;
            }

            var bufferedTicks = _count * TimeSpan.TicksPerSecond / _bytesPerSecond;
            if (bufferedTicks <= threshold.Ticks)
            {
                return false;
            }

            var targetCount = (int)Math.Min(
                (long)_count,
                (long)threshold.Ticks * _bytesPerSecond / TimeSpan.TicksPerSecond);

            var toRemove = _count - targetCount;
            _head = (_head + toRemove) % _capacity;
            _count = targetCount;
            return true;
        }

        /// <summary>
        /// Empties the buffer completely. Safe to repeat, and the buffer stays
        /// usable for subsequent writes and reads.
        /// </summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
        }
    }
}
