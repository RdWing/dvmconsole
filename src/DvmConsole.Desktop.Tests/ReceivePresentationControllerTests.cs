using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceivePresentationControllerTests
{
    [Fact]
    public async Task BackgroundTrafficIsBatchedAndUiTrafficIsImmediate()
    {
        await using var system = new SystemViewModel(
            new FneConnectionOptions("Test", "Console", "127.0.0.1", 62031, 1, null, false, null),
            "Test",
            "127.0.0.1:62031");
        var posted = new Queue<Action>();
        var diagnosticsFlags = new List<bool>();
        var controller = new ReceivePresentationController(
            () => false,
            () => false,
            posted.Enqueue,
            (_, _, publishDiagnostics) => diagnosticsFlags.Add(publishDiagnostics));

        for (int index = 0; index < 65; index++)
            controller.Present(system, default);

        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(64, diagnosticsFlags.Count);
        Assert.All(diagnosticsFlags, Assert.False);
        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(65, diagnosticsFlags.Count);

        var immediate = new ReceivePresentationController(
            () => false,
            () => true,
            _ => throw new InvalidOperationException("UI traffic must not be posted."),
            (_, _, publishDiagnostics) => diagnosticsFlags.Add(publishDiagnostics));
        immediate.Present(system, default);

        Assert.True(diagnosticsFlags[^1]);
    }
}
