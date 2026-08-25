using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class LatestUserSettingsWriterTests
{
    [Fact]
    public async Task OneThousandUpdatesCoalesceToOneBackgroundWrite()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-settings-writer-tests",
            Guid.NewGuid().ToString("N"),
            "UserSettings.json");
        var store = new UserSettingsStore(path);
        var settings = new UserSettings();
        // An async xUnit caller is itself a pool thread, so its numeric ID may
        // later execute the worker after this test awaits. Compare against a
        // dedicated scheduling thread object instead.
        Thread? schedulingThread = null;
        int writesOnSchedulingThread = 0;
        int writes = 0;

        await using (var writer = new LatestUserSettingsWriter(
            snapshot =>
            {
                if (ReferenceEquals(Thread.CurrentThread, schedulingThread))
                    Interlocked.Increment(ref writesOnSchedulingThread);
                Interlocked.Increment(ref writes);
                store.SaveSnapshot(snapshot);
            },
            debounce: TimeSpan.FromMilliseconds(25)))
        {
            Exception? schedulingFailure = null;
            schedulingThread = new Thread(() =>
            {
                try
                {
                    for (int index = 0; index < 1_000; index++)
                    {
                        settings.AudioInputGain = index / 1_000d;
                        writer.Schedule(store.CaptureSnapshot(settings));
                    }
                }
                catch (Exception exception)
                {
                    schedulingFailure = exception;
                }
            })
            {
                IsBackground = true,
                Name = "User settings UI-thread sentinel"
            };
            schedulingThread.Start();
            Assert.True(
                schedulingThread.Join(TimeSpan.FromSeconds(2)),
                "The scheduling-thread sentinel did not complete.");
            Assert.Null(schedulingFailure);

            await writer.FlushAsync();
        }

        Assert.InRange(writes, 1, 3);
        Assert.Equal(0, Volatile.Read(ref writesOnSchedulingThread));
        Assert.True(File.Exists(path));
        Assert.Equal(settings.AudioInputGain, store.Load().AudioInputGain);

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public async Task ForcedFlushPersistsLatestSnapshotWithoutDebounceDelay()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-settings-writer-tests",
            Guid.NewGuid().ToString("N"),
            "UserSettings.json");
        var store = new UserSettingsStore(path);
        var settings = new UserSettings { DarkMode = true };
        await using var writer = new LatestUserSettingsWriter(
            store.SaveSnapshot,
            debounce: TimeSpan.FromMinutes(1));

        writer.Schedule(store.CaptureSnapshot(settings));
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(store.Load().DarkMode);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public async Task FlushRetriesTransientFailuresWithinBoundAndPersists()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-settings-writer-tests",
            Guid.NewGuid().ToString("N"),
            "UserSettings.json");
        var store = new UserSettingsStore(path);
        var settings = new UserSettings { DarkMode = true };
        int attempts = 0;
        var reportedFailures = new List<Exception>();
        await using var writer = new LatestUserSettingsWriter(
            snapshot =>
            {
                if (Interlocked.Increment(ref attempts) <= 2)
                    throw new IOException("transient write fault");
                store.SaveSnapshot(snapshot);
            },
            reportedFailures.Add,
            debounce: TimeSpan.Zero,
            maximumWriteAttempts: 3,
            retryDelay: TimeSpan.Zero);

        writer.Schedule(store.CaptureSnapshot(settings));
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, Volatile.Read(ref attempts));
        Assert.Empty(reportedFailures);
        Assert.True(store.Load().DarkMode);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public async Task FailedFlushPropagatesAndALaterRevisionRecovers()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-settings-writer-tests",
            Guid.NewGuid().ToString("N"),
            "UserSettings.json");
        var store = new UserSettingsStore(path);
        bool failWrites = true;
        int attempts = 0;
        var reportedFailures = new List<Exception>();
        await using var writer = new LatestUserSettingsWriter(
            snapshot =>
            {
                Interlocked.Increment(ref attempts);
                if (failWrites)
                    throw new IOException("settings volume unavailable");
                store.SaveSnapshot(snapshot);
            },
            reportedFailures.Add,
            debounce: TimeSpan.Zero,
            maximumWriteAttempts: 3,
            retryDelay: TimeSpan.Zero);

        writer.Schedule(store.CaptureSnapshot(new UserSettings { DarkMode = false }));
        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("settings volume unavailable", failure.Message);
        Assert.Equal(3, Volatile.Read(ref attempts));
        Assert.Single(reportedFailures);

        failWrites = false;
        writer.Schedule(store.CaptureSnapshot(new UserSettings { DarkMode = true }));
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(4, Volatile.Read(ref attempts));
        Assert.True(store.Load().DarkMode);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}
