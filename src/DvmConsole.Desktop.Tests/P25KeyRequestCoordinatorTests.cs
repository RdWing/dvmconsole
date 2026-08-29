using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class P25KeyRequestCoordinatorTests
{
    [Fact]
    public async Task WaitsForConnectionSettlingAndSpacesEveryConfiguredRequest()
    {
        var delays = new List<TimeSpan>();
        var sent = new List<(byte AlgorithmId, ushort KeyId)>();
        await using var coordinator = new P25KeyRequestCoordinator((delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        (byte AlgorithmId, ushort KeyId)[] requests =
        [
            (0x84, 0x0020),
            (0x84, 0x0050),
            (0x84, 0x069D)
        ];

        await coordinator.Schedule(
            "SKYNET",
            requests,
            () => true,
            (algorithmId, keyId) => sent.Add((algorithmId, keyId)));

        Assert.Equal(requests, sent);
        Assert.Equal(
            [
                P25KeyRequestCoordinator.StartupDelay,
                P25KeyRequestCoordinator.RequestSpacing,
                P25KeyRequestCoordinator.RequestSpacing
            ],
            delays);
    }

    [Fact]
    public async Task StopsPacedRequestsWhenTheConnectionIsLost()
    {
        bool connected = true;
        int delayCount = 0;
        var sent = new List<ushort>();
        await using var coordinator = new P25KeyRequestCoordinator((_, _) =>
        {
            delayCount++;
            if (delayCount == 2)
                connected = false;
            return Task.CompletedTask;
        });

        await coordinator.Schedule(
            "SKYNET",
            [(0x84, 0x0020), (0x84, 0x0050)],
            () => connected,
            (_, keyId) => sent.Add(keyId));

        Assert.Equal([(ushort)0x0020], sent);
    }

    [Fact]
    public async Task AFailedRequestDoesNotPreventLaterKeysFromBeingRequested()
    {
        var sent = new List<ushort>();
        var failures = new List<Exception>();
        await using var coordinator = new P25KeyRequestCoordinator((_, _) => Task.CompletedTask);

        await coordinator.Schedule(
            "SKYNET",
            [(0x84, 0x0020), (0x84, 0x0050)],
            () => true,
            (_, keyId) =>
            {
                if (keyId == 0x0020)
                    throw new InvalidOperationException("first request failed");
                sent.Add(keyId);
            },
            failures.Add);

        Assert.Equal([(ushort)0x0050], sent);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }

    [Fact]
    public async Task AQueuedStatusCallbackCannotScheduleAfterSessionDisposal()
    {
        var sent = new List<ushort>();
        var coordinator = new P25KeyRequestCoordinator((_, _) => Task.CompletedTask);
        await coordinator.DisposeAsync();

        await coordinator.Schedule(
            "SKYNET",
            [(0x84, 0x0020)],
            () => true,
            (_, keyId) => sent.Add(keyId));

        Assert.Empty(sent);
    }
}
