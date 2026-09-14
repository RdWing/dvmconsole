// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Storage.Tests;

public sealed class RecordingDiagnosticsTests
{
    [Fact]
    public async Task SharedTarQualificationFinalizesAndReopensSyntheticMedia()
    {
        string result = await RecordingDiagnostics.RunAsync(Path.GetTempPath());
        Assert.StartsWith("PASS\n", result);
    }
}
