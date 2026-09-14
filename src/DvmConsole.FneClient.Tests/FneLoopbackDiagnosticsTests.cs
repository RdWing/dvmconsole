// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class FneLoopbackDiagnosticsTests
{
    [Fact]
    public async Task RealUdpPeerAuthenticatesStopsAndReconnects()
    {
        string report = await FneLoopbackDiagnostics.RunAsync();
        Assert.StartsWith("PASS\n", report, StringComparison.Ordinal);
    }
}
