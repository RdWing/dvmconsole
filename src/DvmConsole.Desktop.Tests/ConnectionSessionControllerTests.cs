using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConnectionSessionControllerTests
{
    [Fact]
    public async Task ConnectAndDisconnectPreserveLifecycleOrdering()
    {
        var operations = new List<string>();
        var controller = new ConnectionSessionController(
            [],
            () =>
            {
                operations.Add("sync-patch");
                return Task.CompletedTask;
            },
            () =>
            {
                operations.Add("stop-patch-decode");
                return Task.CompletedTask;
            },
            () => operations.Add("stop-patch-forwarding"),
            value => operations.Add($"busy-{value}"),
            value => operations.Add($"status-{value}"),
            _ => throw new InvalidOperationException("No systems are configured."),
            (_, _) => throw new InvalidOperationException("No systems are configured."));

        await controller.ConnectAsync();
        await controller.DisconnectAsync();

        Assert.Equal(
            [
                "busy-True",
                "status-Starting FNE connection services...",
                "sync-patch",
                "status-FNE connection services started; waiting for login acknowledgements.",
                "busy-False",
                "busy-True",
                "status-Stopping FNE connection services...",
                "stop-patch-decode",
                "stop-patch-forwarding",
                "status-FNE connections stopped.",
                "busy-False"
            ],
            operations);
    }
}
