// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ResponsiveStatusControllerTests
{
    [Theory]
    [InlineData(999, 1, true)]
    [InlineData(1000, 1, false)]
    [InlineData(1249, 1.25, true)]
    [InlineData(1250, 1.25, false)]
    public void SelectsOneStatusPresentationByLogicalWidth(
        double width,
        double scale,
        bool expectedCompact)
    {
        Assert.Equal(expectedCompact, ResponsiveStatusPolicy.IsCompact(width, scale));
    }
}
