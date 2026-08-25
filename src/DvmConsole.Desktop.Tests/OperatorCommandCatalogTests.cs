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

}
