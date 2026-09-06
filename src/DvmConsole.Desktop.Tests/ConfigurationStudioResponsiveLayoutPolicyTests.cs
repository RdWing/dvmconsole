// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioResponsiveLayoutPolicyTests
{
    [Theory]
    [InlineData(1488, false, false, false)]
    [InlineData(1180, false, false, false)]
    [InlineData(880, false, true, false)]
    [InlineData(720, false, true, false)]
    [InlineData(600, false, true, false)]
    [InlineData(599, true, true, false)]
    [InlineData(390, true, true, true)]
    [InlineData(360, true, true, true)]
    public void UsesTheAlternateListOnlyAtPhoneWidths(
        double width,
        bool expectedPhoneList,
        bool expectedStackedInspector,
        bool expectedTouchTargets)
    {
        ConfigurationStudioResponsiveLayout result =
            ConfigurationStudioResponsiveLayoutPolicy.Evaluate(width, 800);

        Assert.Equal(expectedPhoneList, result.UsePhoneChannelList);
        Assert.Equal(expectedStackedInspector, result.StackInspector);
        Assert.Equal(expectedTouchTargets, result.UseTouchTargets);
        if (expectedPhoneList)
            Assert.Equal(320, result.PreviewMaximumHeight);
        else
            Assert.True(double.IsPositiveInfinity(result.PreviewMaximumHeight));
    }
}
