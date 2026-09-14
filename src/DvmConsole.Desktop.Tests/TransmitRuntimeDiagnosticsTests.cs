// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class TransmitRuntimeDiagnosticsTests
{
    [Fact]
    public async Task DigitalTransmitQualificationRunsThroughDesktopNativeBindings()
    {
        string report = await TransmitRuntimeDiagnostics.RunAsync(() => new SoftwareVocoderBackend());
        Assert.StartsWith("PASS\n", report);
    }
}
