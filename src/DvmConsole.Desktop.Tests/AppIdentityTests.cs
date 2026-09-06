// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AppIdentityTests
{
    [Fact]
    public void AvaloniaApplicationNameMatchesProductName()
    {
        var app = new App();
        app.Initialize();

        Assert.Equal("DVM Console NEO", app.Name);
    }
}
