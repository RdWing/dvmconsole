// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class OperatorCommandControllerTests
{
    [Fact]
    public async Task RoutesWindowCommandsWithoutMenuOrWindowDependencies()
    {
        var surface = new RecordingSurface();
        var controller = new OperatorCommandController(surface);

        await controller.ExecuteAsync(OperatorCommandIds.Documentation);
        await controller.ExecuteAsync(OperatorCommandIds.About);
        await controller.ExecuteAsync(OperatorCommandIds.DebugLogs);

        Assert.Equal(["documentation", "about", "debug"], surface.Calls);
    }

    [Fact]
    public async Task RoutesToolAndSubscriberCommandsThroughTypedSurfaceMethods()
    {
        var surface = new RecordingSurface();
        var controller = new OperatorCommandController(surface);
        OperatorToolSectionDefinition history = OperatorToolSectionCatalog.All.Single(
            definition => definition.Section == OperatorToolSection.History);

        await controller.ExecuteAsync(history.CommandId);
        await controller.ExecuteAsync(OperatorCommandIds.SubscriberRadioCheck);

        Assert.Equal(["tool:History", "subscriber:RadioCheck"], surface.Calls);
    }

    private sealed class RecordingSurface : IOperatorCommandSurface
    {
        public List<string> Calls { get; } = [];
        public MainWindowViewModel Session => throw new InvalidOperationException("Not used by this test.");
        public Task OpenSubscriberCommandAsync(P25SubscriberCommand command)
        {
            Calls.Add($"subscriber:{command}");
            return Task.CompletedTask;
        }
        public void OpenTool(OperatorToolSection section) => Calls.Add($"tool:{section}");
        public void ShowDebugLogs() => Calls.Add("debug");
        public void ToggleEngineeringHealth() => Calls.Add("health");
        public void ShowDocumentation() => Calls.Add("documentation");
        public void ShowAbout() => Calls.Add("about");
    }
}
