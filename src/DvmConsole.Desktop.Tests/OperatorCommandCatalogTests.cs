// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class OperatorCommandCatalogTests
{
    [Fact]
    public async Task ExecutesTheCommandMappedToAnIdCaseInsensitively()
    {
        int executions = 0;
        var catalog = new OperatorCommandCatalog(
        [
            new OperatorCommandDefinition(
                "audio.input",
                () =>
                {
                    executions++;
                    return Task.CompletedTask;
                })
        ]);

        await catalog.ExecuteAsync("AUDIO.INPUT");
        await catalog.ExecuteAsync("missing");

        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task DisabledCommandIsNeverExecuted()
    {
        int executions = 0;
        var catalog = new OperatorCommandCatalog(
        [
            new OperatorCommandDefinition(
                "disabled",
                () =>
                {
                    executions++;
                    return Task.CompletedTask;
                },
                () => false)
        ]);

        await catalog.ExecuteAsync("disabled");
        await catalog.ExecuteAsync("missing");

        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task CurrentTargetBindingFollowsASessionReplacement()
    {
        var first = new CommandTarget();
        var replacement = new CommandTarget();
        CommandTarget current = first;
        var command = OperatorCommandDefinition.BindCurrent(
            "receive.enable",
            () => current,
            target => target.ExecuteAsync(),
            target => target.Enabled);

        await command.ExecuteAsync();
        current = replacement;
        await command.ExecuteAsync();

        Assert.Equal(1, first.Executions);
        Assert.Equal(1, replacement.Executions);
    }

    private sealed class CommandTarget
    {
        public bool Enabled { get; set; } = true;
        public int Executions { get; private set; }

        public Task ExecuteAsync()
        {
            Executions++;
            return Task.CompletedTask;
        }
    }

}
