// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioNavigationStateTests
{
    [Fact]
    public void OwnsTheSectionCatalogAndRejectsForeignItems()
    {
        var state = new ConfigurationStudioNavigationState(ConfigurationStudioSection.Zones);

        Assert.Equal(7, state.Items.Count);
        Assert.True(state.Is(ConfigurationStudioSection.Zones));
        Assert.True(state.Select(state.Find(ConfigurationStudioSection.Streams)));
        Assert.True(state.Is(ConfigurationStudioSection.Streams));
        Assert.False(state.Select(state.Current));
        Assert.Throws<ArgumentException>(() => state.Select(
            new ConfigurationStudioNavigationItem(
                ConfigurationStudioSection.Overview,
                "Foreign",
                "Not owned by this session")));
    }
}
