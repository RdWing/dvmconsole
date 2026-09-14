// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class PatchSourceSelectionPolicyTests
{
    [Fact]
    public void UsesRoutingOrderDeduplicatesSourcesAndIgnoresInactiveGroups()
    {
        ChannelId alpha = Id("Alpha", 100);
        ChannelId beta = Id("Beta", 200);
        ChannelId gamma = Id("Gamma", 300);
        ConsoleGroupDefinitionSnapshot[] groups =
        [
            new("One way", false, [beta, alpha], true, true, 0),
            new("Two way", false, [alpha, beta], true, false, 0),
            new("Disabled", false, [gamma], false, false, 0),
            new("Multi-select", true, [gamma], true, false, 0),
            new("Empty", false, [], true, true, 0)
        ];
        Assert.Equal([beta, alpha], PatchSourceSelectionPolicy.SelectEnabledSources(groups));
        Assert.Empty(PatchSourceSelectionPolicy.SelectEnabledSources([]));
    }

    private static ChannelId Id(string name, uint destination)
        => ConsoleChannelState.GetId(new ChannelRuntimeDefinition(name, "Test", "p25", destination, 0));
}
