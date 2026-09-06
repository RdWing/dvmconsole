// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class BoundedPcmBufferPoolTests
{
    [Fact]
    public void ReusesClearedFramesAndBoundsRetainedMemory()
    {
        var pool = new BoundedPcmBufferPool(bufferLength: 160, maximumRetained: 2);
        short[] first = pool.Rent();
        short[] second = pool.Rent();
        short[] excess = pool.Rent();
        first[0] = 9;

        pool.Return(first);
        pool.Return(second);
        pool.Return(excess);

        Assert.Equal(2, pool.RetainedCount);
        short[] reused = pool.Rent();
        Assert.Equal(0, reused[0]);
        Assert.Equal(1, pool.RetainedCount);
    }

    [Fact]
    public void RejectsForeignSizedBuffer()
    {
        var pool = new BoundedPcmBufferPool(bufferLength: 160, maximumRetained: 2);

        Assert.Throws<ArgumentException>(() => pool.Return(new short[80]));
    }
}
