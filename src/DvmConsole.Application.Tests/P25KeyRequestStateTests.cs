// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class P25KeyRequestStateTests
{
    [Fact]
    public void DeduplicatesByAlgorithmAndKeyButRetriesUntilResponse()
    {
        var state = new P25KeyRequestState();
        var sends = new List<bool>();
        var port = new P25KeyRequestPort(state, (_, _) => { }, (_, _, retry) => sends.Add(retry));
        port.Request(1, 2);
        port.Request(1, 2);
        port.Request(2, 2);
        port.Retry(1, 2);
        Assert.Equal(new[] { false, false, true }, sends);
        state.ObserveResponse(1, 2);
        port.Retry(1, 2);
        Assert.Equal(3, sends.Count);
        Assert.True(port.HasResponse(1, 2));
        Assert.False(port.HasResponse(2, 2));
    }

    [Fact]
    public void FailedSendCanBeRequestedAgainAndResetStartsFreshConnection()
    {
        var state = new P25KeyRequestState();
        Assert.Throws<IOException>(() => state.Request(1, 2, () => throw new IOException()));
        int sends = 0;
        state.Request(1, 2, () => sends++);
        state.ObserveResponse(1, 2);
        state.Clear();
        Assert.False(state.HasResponse(1, 2));
        state.Request(1, 2, () => sends++);
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task ConcurrentRequestsReserveOneInitialSend()
    {
        var state = new P25KeyRequestState();
        int sends = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            state.Request(1, 2, () => Interlocked.Increment(ref sends)))));
        Assert.Equal(1, sends);
    }
}
