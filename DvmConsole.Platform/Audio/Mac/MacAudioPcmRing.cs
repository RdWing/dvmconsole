// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DvmConsole.Platform.Audio.Mac
{
    /// <summary>
    /// A bounded single-producer/single-consumer byte ring. The producer and
    /// consumer positions are published independently so the CoreAudio callback
    /// path performs no managed allocation or locking.
    /// </summary>
    internal sealed class MacAudioPcmRing
    {
        private readonly byte[] _storage;
        private readonly object _writerGate = new();
        private long _writePosition;
        private long _readPosition;

        internal MacAudioPcmRing(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _storage = new byte[capacity];
        }

        internal int Capacity => _storage.Length;

        internal int BufferedBytes
        {
            get
            {
                var write = Volatile.Read(ref _writePosition);
                var read = Volatile.Read(ref _readPosition);
                var count = write - read;
                return (int)Math.Clamp(count, 0, _storage.Length);
            }
        }

        internal bool TryWrite(ReadOnlyMemory<byte> data)
        {
            if (data.Length == 0)
            {
                return true;
            }

            lock (_writerGate)
            {
                var write = _writePosition;
                var read = Volatile.Read(ref _readPosition);
                if (data.Length > _storage.Length - (write - read))
                {
                    return false;
                }

                CopyManagedToRing(data, write);
                Volatile.Write(ref _writePosition, write + data.Length);
                return true;
            }
        }

        internal bool TryWriteFromNative(IntPtr source, int byteCount)
        {
            if (byteCount <= 0)
            {
                return true;
            }

            var write = _writePosition;
            var read = Volatile.Read(ref _readPosition);
            if (byteCount > _storage.Length - (write - read))
            {
                return false;
            }

            CopyNativeToRing(source, byteCount, write);
            Volatile.Write(ref _writePosition, write + byteCount);
            return true;
        }

        internal int ReadInto(byte[] destination, int requestedBytes)
        {
            if (destination is null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            var write = Volatile.Read(ref _writePosition);
            var read = _readPosition;
            var available = (int)Math.Clamp(write - read, 0, _storage.Length);
            var count = Math.Min(Math.Min(available, requestedBytes), destination.Length);
            if (count <= 0)
            {
                return 0;
            }

            CopyRingToManaged(read, destination, count);
            Volatile.Write(ref _readPosition, read + count);
            return count;
        }

        internal int ReadToNative(IntPtr destination, int requestedBytes)
        {
            var write = Volatile.Read(ref _writePosition);
            var read = _readPosition;
            var available = (int)Math.Clamp(write - read, 0, _storage.Length);
            var count = Math.Min(available, requestedBytes);
            if (count <= 0)
            {
                return 0;
            }

            CopyRingToNative(read, destination, count);
            Volatile.Write(ref _readPosition, read + count);
            return count;
        }

        internal void Clear()
        {
            Volatile.Write(ref _readPosition, Volatile.Read(ref _writePosition));
        }

        private void CopyManagedToRing(ReadOnlyMemory<byte> source, long position)
        {
            var sourceOffset = 0;
            var remaining = source.Length;
            var offset = (int)(position % _storage.Length);
            while (remaining > 0)
            {
                var count = Math.Min(remaining, _storage.Length - offset);
                source.Span.Slice(sourceOffset, count).CopyTo(_storage.AsSpan(offset, count));
                sourceOffset += count;
                remaining -= count;
                offset = 0;
            }
        }

        private void CopyNativeToRing(IntPtr source, int byteCount, long position)
        {
            var sourceOffset = 0;
            var remaining = byteCount;
            var offset = (int)(position % _storage.Length);
            while (remaining > 0)
            {
                var count = Math.Min(remaining, _storage.Length - offset);
                Marshal.Copy(IntPtr.Add(source, sourceOffset), _storage, offset, count);
                sourceOffset += count;
                remaining -= count;
                offset = 0;
            }
        }

        private void CopyRingToManaged(long position, byte[] destination, int byteCount)
        {
            var destinationOffset = 0;
            var remaining = byteCount;
            var offset = (int)(position % _storage.Length);
            while (remaining > 0)
            {
                var count = Math.Min(remaining, _storage.Length - offset);
                Buffer.BlockCopy(_storage, offset, destination, destinationOffset, count);
                destinationOffset += count;
                remaining -= count;
                offset = 0;
            }
        }

        private void CopyRingToNative(long position, IntPtr destination, int byteCount)
        {
            var destinationOffset = 0;
            var remaining = byteCount;
            var offset = (int)(position % _storage.Length);
            while (remaining > 0)
            {
                var count = Math.Min(remaining, _storage.Length - offset);
                Marshal.Copy(_storage, offset, IntPtr.Add(destination, destinationOffset), count);
                destinationOffset += count;
                remaining -= count;
                offset = 0;
            }
        }
    }
}
