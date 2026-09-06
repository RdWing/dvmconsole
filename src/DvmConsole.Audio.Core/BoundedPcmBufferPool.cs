// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;

namespace DvmConsole.Audio;

/// <summary>
/// Reuses a bounded number of fixed-size PCM buffers without allowing a burst
/// to permanently grow retained memory.
/// </summary>
public sealed class BoundedPcmBufferPool
{
    private readonly ConcurrentStack<short[]> buffers = new();
    private readonly int bufferLength;
    private readonly int maximumRetained;
    private int retainedCount;

    public BoundedPcmBufferPool(int bufferLength, int maximumRetained)
    {
        if (bufferLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferLength));
        if (maximumRetained <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRetained));
        this.bufferLength = bufferLength;
        this.maximumRetained = maximumRetained;
    }

    public int BufferLength => bufferLength;
    public int RetainedCount => Volatile.Read(ref retainedCount);

    public short[] Rent()
    {
        if (!buffers.TryPop(out short[]? buffer))
            return new short[bufferLength];
        Interlocked.Decrement(ref retainedCount);
        return buffer;
    }

    public void Return(short[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length != bufferLength)
            throw new ArgumentException("The PCM buffer does not belong to this pool.", nameof(buffer));
        buffer.AsSpan().Clear();
        if (Interlocked.Increment(ref retainedCount) <= maximumRetained)
        {
            buffers.Push(buffer);
            return;
        }
        Interlocked.Decrement(ref retainedCount);
    }
}
