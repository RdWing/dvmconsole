// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Desktop;
using DvmConsole.Application;
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
    public async Task ServicesReportNamedDisposalDurationsWithoutChangingOrder()
    {
        var observed = new List<ConsoleSessionServiceDisposalTiming>();
        var services = new ConsoleSessionServices(observed.Add);
        services.Presentation.Register("first", NoOp);
        services.Receive.Register("second", async () =>
        {
            await Task.Delay(10);
        });

        await services.DisposeAsync();

        Assert.Equal(["second", "first"], observed.Select(item => item.Name));
        Assert.Equal(["receive", "presentation"], observed.Select(item => item.Scope));
        Assert.All(observed, item => Assert.True(item.Duration >= TimeSpan.Zero));
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
    public async Task StalledServiceDoesNotPreventLaterCleanupOwners()
    {
        var stalled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var laterCleanupRan = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ConsoleSessionServices(
            disposalStepTimeout: TimeSpan.FromMilliseconds(25),
            overallDisposalTimeout: TimeSpan.FromMilliseconds(250));
        services.Presentation.Register("later", () =>
        {
            laterCleanupRan.TrySetResult();
            return ValueTask.CompletedTask;
        });
        services.Audio.Register("stalled", () => new ValueTask(stalled.Task));

        try
        {
            Exception? failure = await Record.ExceptionAsync(() =>
                services.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.NotNull(failure);
            IReadOnlyList<Exception> failures = failure is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions
                : [failure];
            Assert.All(failures, item => Assert.IsType<TimeoutException>(item));
            Assert.Contains(failures, item => item.Message.Contains("audio/stalled", StringComparison.Ordinal));

            // Later cleanup may also exceed its short budget on a busy runner.
            // It must still run while the first owner remains blocked.
            await laterCleanupRan.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stalled.Task.IsCompleted);
            await WaitForAsync(() => services.AbandonedOperationCount == 1);
        }
        finally
        {
            stalled.TrySetResult();
            await WaitForAsync(() => services.AbandonedOperationCount == 0);
        }
    }

    [Fact]
    public async Task FaultedAbandonedServiceIsContainedAndReleasedFromOwnership()
    {
        var stalled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ConsoleSessionServices(
            disposalStepTimeout: TimeSpan.FromMilliseconds(25),
            overallDisposalTimeout: TimeSpan.FromMilliseconds(250));
        services.Audio.Register("stalled", () => new ValueTask(stalled.Task));

        await Assert.ThrowsAsync<TimeoutException>(() => services.DisposeAsync().AsTask());
        Assert.Equal(1, services.AbandonedOperationCount);

        stalled.TrySetException(new InvalidOperationException("eventual cleanup failure"));
        await WaitForAsync(() => services.AbandonedOperationCount == 0);
    }

    [Fact]
    public async Task FaultedServiceIdentifiesItsOwnershipScopeAndName()
    {
        var services = new ConsoleSessionServices();
        var expected = new IOException("test cleanup failure");
        services.Audio.Register(
            "output-route",
            () => ValueTask.FromException(expected));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => services.DisposeAsync().AsTask());

        Assert.Contains("audio/output-route", failure.Message, StringComparison.Ordinal);
        Assert.Same(expected, failure.InnerException);
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
    public async Task RuntimeCanOwnAnIdleTimerUntilActivityStartsIt()
    {
        var services = new ConsoleSessionServices();
        var runtime = new ConsoleSessionRuntime(services);

        ConsoleSessionRuntime.ConsoleSessionTimer timer = runtime.CreateTimer(
            TimeSpan.FromHours(1),
            static (_, _) => { },
            startImmediately: false);

        Assert.Equal(1, runtime.ActiveTimerCount);
        Assert.False(timer.IsRunning);
        timer.Start();
        Assert.True(timer.IsRunning);
        timer.Stop();
        Assert.False(timer.IsRunning);

        await runtime.DisposeAsync();
        Assert.Equal(0, runtime.ActiveTimerCount);
    }

    [Fact]
    public async Task RuntimeUsesTheInjectedApplicationScheduler()
    {
        var services = new ConsoleSessionServices();
        var scheduler = new FakeApplicationScheduler();
        var runtime = new ConsoleSessionRuntime(services, scheduler);
        int ticks = 0;

        ConsoleSessionRuntime.ConsoleSessionTimer timer = runtime.CreateTimer(
            TimeSpan.FromSeconds(3),
            (_, _) => ticks++,
            startImmediately: false);

        FakeScheduledWork work = Assert.Single(scheduler.Works);
        Assert.Equal(TimeSpan.FromSeconds(3), work.Interval);
        Assert.False(timer.IsRunning);
        timer.Start();
        await work.FireAsync();
        Assert.Equal(1, ticks);

        await runtime.DisposeAsync();
        Assert.True(work.IsDisposed);
    }

    [Fact]
    public async Task RuntimeReportsTimerFaultsWithoutEscapingTheSchedulerCallback()
    {
        var services = new ConsoleSessionServices();
        var scheduler = new FakeApplicationScheduler();
        var failures = new List<Exception>();
        var runtime = new ConsoleSessionRuntime(services, scheduler, failures.Add);
        InvalidOperationException expected = new("timer failed");

        _ = runtime.CreateTimer(
            TimeSpan.FromSeconds(1),
            (_, _) => throw expected,
            startImmediately: true);

        await Assert.Single(scheduler.Works).FireAsync();

        Assert.Same(expected, Assert.Single(failures));
        await runtime.DisposeAsync();
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
                new MainWindowViewModelOptions(
                    Document: new(new UserSettingsStore(Path.Combine(root, "UserSettings.json"))),
                    Host: new(SerialPortProvider: () => [], SessionServices: services),
                    Features: new(NetworkDisabledDemo: true)));

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
                "radio-session-ingress",
                "systems",
                "audio-work",
                "source-receive-work",
                "call-recording-manager",
                "debug-log-workspace",
                "shell-settings"
            ];
            Assert.All(requiredOwnership, name => Assert.Contains(name, cleanupOrder));
            Assert.Equal("dispatcher-timers", cleanupOrder[0]);
            Assert.Equal("systems", cleanupOrder[^1]);
            AssertBefore(cleanupOrder, "ptt-session", "coordinators-under-ptt-gate");
            AssertBefore(cleanupOrder, "radio-session-ingress", "systems");
            AssertBefore(cleanupOrder, "audio-work", "source-receive-work");
            AssertBefore(cleanupOrder, "shell-settings", "systems");
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

    [Fact]
    public void OwnedCollectionConstructionRollsBackEarlierResourcesInReverseOrder()
    {
        var disposalOrder = new List<int>();

        FormatException failure = Assert.Throws<FormatException>(() =>
            OwnedResourceCollectionBuilder.Create(
                3,
                index => index == 2
                    ? throw new FormatException("synthetic collection failure")
                    : new TrackedAsyncDisposable(index, disposalOrder)));

        Assert.Equal("synthetic collection failure", failure.Message);
        Assert.Equal([1, 0], disposalOrder);
    }

    [Fact]
    public void PartiallyConstructedMainWindowRollsBackWithoutMaskingTheOriginalFailure()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-partial-session-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = new ConsoleSessionServices();
        try
        {
            FormatException failure = Assert.Throws<FormatException>(() =>
                ConsoleSessionConstruction.Create(
                    services,
                    () => new MainWindowViewModel(
                        "Test session",
                        [],
                        [],
                        new MainWindowViewModelOptions(
                            Document: new(new UserSettingsStore(
                                Path.Combine(root, "UserSettings.json"))),
                            Host: new(
                                SerialPortProvider: () => throw new FormatException("serial discovery failed"),
                                SessionServices: services),
                            Features: new(NetworkDisabledDemo: true)))));

            Assert.Equal("serial discovery failed", failure.Message);
            Assert.Equal(0, services.Count);
            Assert.Throws<ObjectDisposedException>(() =>
                services.Audio.Register("late-registration", () => ValueTask.CompletedTask));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ValueTask NoOp()
        => ValueTask.CompletedTask;

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeApplicationScheduler : IApplicationScheduler
    {
        public List<FakeScheduledWork> Works { get; } = [];

        public IScheduledWork CreatePeriodic(
            TimeSpan interval,
            Func<CancellationToken, ValueTask> callback,
            bool startImmediately = true)
        {
            var work = new FakeScheduledWork(interval, callback, startImmediately);
            Works.Add(work);
            return work;
        }
    }

    private sealed class FakeScheduledWork(
        TimeSpan interval,
        Func<CancellationToken, ValueTask> callback,
        bool startImmediately) : IScheduledWork
    {
        public TimeSpan Interval { get; } = interval;
        public bool IsRunning { get; private set; } = startImmediately;
        public bool IsDisposed { get; private set; }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            IsRunning = true;
        }

        public void Stop() => IsRunning = false;

        public async ValueTask FireAsync()
        {
            if (IsRunning)
                await callback(CancellationToken.None);
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackedAsyncDisposable(
        int id,
        ICollection<int> disposalOrder) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            disposalOrder.Add(id);
            return ValueTask.CompletedTask;
        }
    }

    private static void AssertBefore(string[] values, string first, string second)
        => Assert.True(
            Array.IndexOf(values, first) < Array.IndexOf(values, second),
            $"Expected {first} to be disposed before {second}.");
}
