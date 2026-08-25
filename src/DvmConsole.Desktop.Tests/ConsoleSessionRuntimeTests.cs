using DvmConsole.Desktop;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConsoleSessionRuntimeTests
{
    [Fact]
    public async Task ServicesDisposeInReverseConstructionOrder()
    {
        var order = new List<string>();
        var services = new ConsoleSessionServices();
        services.Presentation.Register("first", () =>
        {
            order.Add("first");
            return ValueTask.CompletedTask;
        });
        services.Audio.Register("second", () =>
        {
            order.Add("second");
            return ValueTask.CompletedTask;
        });

        await services.DisposeAsync();

        Assert.Equal(["second", "first"], order);
    }

    [Fact]
    public async Task ServicesPublishNamedOperationalOwnershipScopes()
    {
        var services = new ConsoleSessionServices();
        services.Audio.Register("audio", NoOp);
        services.Receive.Register("receive", NoOp);
        services.Transmit.Register("transmit", NoOp);
        services.Recording.Register("recording", NoOp);
        services.Patch.Register("patch", NoOp);
        services.Connection.Register("connection", NoOp);
        services.Presentation.Register("presentation", NoOp);

        ConsoleSessionServiceOwnership[] ownership = services
            .SnapshotOwnership()
            .ToArray();

        Assert.Equal(
            ["audio", "receive", "transmit", "recording", "patch", "connection", "presentation"],
            ownership.Select(entry => entry.Scope));
        Assert.Equal(
            ["audio", "receive", "transmit", "recording", "patch", "connection", "presentation"],
            ownership.Select(entry => entry.Name));

        await services.DisposeAsync();
    }

    [Fact]
    public async Task ServicesPublishOneNamedCleanupToConcurrentDisposalCallers()
    {
        var cleanupStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ConsoleSessionServices();
        int cleanupCount = 0;
        services.Receive.Register(
            "audio-work",
            () => new ValueTask(BlockCleanupAsync()));

        Task first = services.DisposeAsync().AsTask();
        await cleanupStarted.Task;
        Task second = services.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() =>
            services.Audio.Register("late", NoOp));
        allowCleanup.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, cleanupCount);

        async Task BlockCleanupAsync()
        {
            Interlocked.Increment(ref cleanupCount);
            cleanupStarted.SetResult();
            await allowCleanup.Task;
        }
    }

    [Fact]
    public async Task RuntimeStopsOwnedTimersBeforeSessionCleanup()
    {
        var cleanupCalled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ConsoleSessionServices();
        ConsoleSessionRuntime? runtime = null;
        int activeTimersAtSessionCleanup = -1;
        services.Presentation.Register("session", () =>
        {
            activeTimersAtSessionCleanup = runtime!.ActiveTimerCount;
            cleanupCalled.SetResult();
            return ValueTask.CompletedTask;
        });
        runtime = new ConsoleSessionRuntime(services);
        runtime.StartTimer(TimeSpan.FromHours(1), static (_, _) => { });
        runtime.StartTimer(TimeSpan.FromHours(2), static (_, _) => { });

        ConsoleSessionServiceOwnership[] ownership = services
            .SnapshotOwnership()
            .ToArray();
        Assert.Equal(2, runtime.ActiveTimerCount);
        Assert.Equal(new ConsoleSessionServiceOwnership("timers", "dispatcher-timers"), ownership[^1]);

        await runtime.DisposeAsync();

        Assert.True(cleanupCalled.Task.IsCompletedSuccessfully);
        Assert.Equal(0, activeTimersAtSessionCleanup);
        Assert.Equal(0, runtime.ActiveTimerCount);
        Assert.Equal(0, services.Count);
    }

    [Fact]
    public async Task MainWindowFacadeRegistersNamedScopesWithoutMonolithicCleanup()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-session-ownership-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = new ConsoleSessionServices();
        MainWindowViewModel? viewModel = null;
        try
        {
            viewModel = new MainWindowViewModel(
                "Test session",
                [],
                [],
                userSettingsStore: new UserSettingsStore(Path.Combine(root, "UserSettings.json")),
                serialPortProvider: () => [],
                sessionServices: services,
                networkDisabledDemo: true);

            ConsoleSessionServiceOwnership[] ownership = services
                .SnapshotOwnership()
                .ToArray();
            string[] requiredScopes =
                ["audio", "receive", "transmit", "recording", "patch", "connection", "presentation"];

            Assert.All(
                requiredScopes,
                scope => Assert.Contains(ownership, entry => entry.Scope == scope));
            Assert.DoesNotContain(
                ownership,
                entry => entry.Name.Equals("main-window-view-model", StringComparison.Ordinal));
            string[] cleanupOrder = ownership
                .Reverse()
                .Select(entry => entry.Name)
                .ToArray();
            string[] requiredOwnership =
            [
                "dispatcher-timers",
                "ptt-session",
                "coordinators-under-ptt-gate",
                "systems",
                "audio-work",
                "source-receive-work",
                "call-recording-manager",
                "user-settings-writer"
            ];
            Assert.All(requiredOwnership, name => Assert.Contains(name, cleanupOrder));
            Assert.Equal("dispatcher-timers", cleanupOrder[0]);
            Assert.Equal("user-settings-writer", cleanupOrder[^1]);
            AssertBefore(cleanupOrder, "ptt-session", "coordinators-under-ptt-gate");
            AssertBefore(cleanupOrder, "audio-work", "source-receive-work");
        }
        finally
        {
            if (viewModel is not null)
                await viewModel.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MainWindowConstructionFailureRollsBackSessionOwnership()
    {
        var services = new ConsoleSessionServices();
        bool markerDisposed = false;
        services.Presentation.Register(
            "construction-test-marker",
            () =>
            {
                markerDisposed = true;
                return ValueTask.CompletedTask;
            });

        Assert.Throws<FormatException>(() => ConsoleSessionConstruction.Create<object>(
            services,
            () => throw new FormatException("synthetic construction failure")));

        Assert.True(markerDisposed);
        Assert.Equal(0, services.Count);
        Assert.Throws<ObjectDisposedException>(() =>
            services.Presentation.Register("late-registration", () => ValueTask.CompletedTask));
    }

    private static ValueTask NoOp()
        => ValueTask.CompletedTask;

    private static void AssertBefore(string[] values, string first, string second)
        => Assert.True(
            Array.IndexOf(values, first) < Array.IndexOf(values, second),
            $"Expected {first} to be disposed before {second}.");
}
