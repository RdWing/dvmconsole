// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class P25KeyRetrievalCoordinatorTests
{
    [Fact]
    public async Task RemoteKeysAreConnectionScopedAndNeverReplaceLocalFallbackPermanently()
    {
        byte[] local = Enumerable.Repeat((byte)0x11, 32).ToArray();
        byte[] remote = Enumerable.Repeat((byte)0x22, 32).ToArray();
        using var keys = new P25KeyRing("First", new KeyContainer
        {
            Keys = [new KeyEntry { AlgId = 0x84, KeyId = 1, Key = Convert.ToHexString(local) }]
        });
        await using var coordinator = new P25KeyRetrievalCoordinator(keys, (_, _) => Task.CompletedTask);
        bool connected = true;
        int sends = 0;
        Task Schedule() => coordinator.Schedule("First", [(0x84, 1)], () => connected,
            (_, _) => sends++, (_, _) => true, (_, _) => sends++, exception => throw exception);
        await Schedule();
        Assert.Equal(1, sends); // Request even with a local fallback.
        Assert.True(coordinator.TryApply("First", 0x84, 1, remote));
        Assert.True(keys.TryResolve("First", 0x84, 1, out var resolved));
        Assert.Equal(remote, resolved.ToArray());
        Assert.False(coordinator.TryApply("Second", 0x84, 1, remote));
        Assert.ThrowsAny<ArgumentException>(() => coordinator.TryApply("First", 0xFF, 1, remote));
        connected = false;
        Assert.False(coordinator.TryApply("First", 0x84, 1, local));
        coordinator.Cancel("First");
        Assert.True(keys.TryResolve("First", 0x84, 1, out resolved));
        Assert.Equal(local, resolved.ToArray());
        connected = true;
        await Schedule();
        Assert.True(coordinator.TryApply("First", 0x84, 1, remote));
        coordinator.Pause();
        Assert.False(coordinator.TryApply("First", 0x84, 1, remote));
        await Schedule();
        Assert.True(coordinator.TryApply("First", 0x84, 1, remote));
        await coordinator.DisposeAsync();
        Assert.False(coordinator.TryApply("First", 0x84, 1, remote));
        Assert.True(keys.TryResolve("First", 0x84, 1, out resolved));
        Assert.Equal(local, resolved.ToArray());
        await Schedule();
        Assert.Equal(3, sends);
    }
}
