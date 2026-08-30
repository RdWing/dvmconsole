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
            _ =>
            {
                operations.Add("sync-patch");
                return Task.CompletedTask;
            },
            _ =>
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

    [Fact]
    public async Task DisconnectCancelsAnInFlightConnectBeforeBecomingTheFinalTransition()
    {
        var connectEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<string>();
        var controller = new ConnectionSessionController(
            [],
            async cancellationToken =>
            {
                operations.Add("sync-patch-start");
                connectEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            _ =>
            {
                operations.Add("stop-patch-decode");
                return Task.CompletedTask;
            },
            () => operations.Add("stop-patch-forwarding"),
            value => operations.Add($"busy-{value}"),
            value => operations.Add($"status-{value}"),
            _ => throw new InvalidOperationException("No systems are configured."),
            (_, _) => throw new InvalidOperationException("No systems are configured."));

        Task connect = controller.ConnectAsync();
        await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Task disconnect = controller.DisconnectAsync();

        await disconnect.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);

        Assert.True(operations.IndexOf("sync-patch-start") < operations.IndexOf("stop-patch-decode"));
        Assert.Equal("busy-False", operations[^1]);
        Assert.Contains("status-FNE connections stopped.", operations);
    }

    [Fact]
    public async Task FailedTransitionReleasesGateForNextDisconnect()
    {
        int stopPatchDecodeCalls = 0;
        var controller = new ConnectionSessionController(
            [],
            _ => Task.FromException(new IOException("synthetic patch synchronization failure")),
            _ =>
            {
                Interlocked.Increment(ref stopPatchDecodeCalls);
                return Task.CompletedTask;
            },
            () => { },
            _ => { },
            _ => { },
            _ => throw new InvalidOperationException("No systems are configured."),
            (_, _) => throw new InvalidOperationException("No systems are configured."));

        await Assert.ThrowsAsync<IOException>(controller.ConnectAsync);
        await controller.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, Volatile.Read(ref stopPatchDecodeCalls));
    }
}
