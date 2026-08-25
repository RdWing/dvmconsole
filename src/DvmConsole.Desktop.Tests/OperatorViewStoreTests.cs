using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class OperatorViewStoreTests
{
    [Fact]
    public void MissingStateDefaultsToHiddenWithoutWritingAFile()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "OperatorView.json");
            var store = new OperatorViewStore(path);

            OperatorViewSettings settings = store.Load();

            Assert.False(settings.EngineeringHealthVisible);
            Assert.Equal(OperatorViewSettings.DefaultEngineeringHealthHeight, settings.EngineeringHealthHeight);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VisibilityAndHeightRoundTripAndHeightIsNormalized()
    {
        string directory = CreateDirectory();
        try
        {
            var store = new OperatorViewStore(Path.Combine(directory, "OperatorView.json"));
            store.Save(new OperatorViewSettings
            {
                EngineeringHealthVisible = true,
                EngineeringHealthHeight = 500
            });

            OperatorViewSettings loaded = store.Load();

            Assert.True(loaded.EngineeringHealthVisible);
            Assert.Equal(OperatorViewSettings.MaximumEngineeringHealthHeight, loaded.EngineeringHealthHeight);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidStateFallsBackToHiddenWithoutOverwritingSource()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "OperatorView.json");
            File.WriteAllText(path, "{ invalid json");
            var store = new OperatorViewStore(path);

            OperatorViewSettings loaded = store.Load();

            Assert.False(loaded.EngineeringHealthVisible);
            Assert.Equal("{ invalid json", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ForcedFlushPersistsTheLatestPendingViewSnapshot()
    {
        string directory = CreateDirectory();
        try
        {
            var store = new OperatorViewStore(Path.Combine(directory, "OperatorView.json"));
            int writes = 0;
            await using var writer = new LatestOperatorViewWriter(
                snapshot =>
                {
                    Interlocked.Increment(ref writes);
                    store.Save(snapshot);
                },
                debounce: TimeSpan.FromMinutes(1));

            writer.Schedule(new OperatorViewSettings
            {
                EngineeringHealthVisible = false,
                EngineeringHealthHeight = 100
            });
            writer.Schedule(new OperatorViewSettings
            {
                EngineeringHealthVisible = true,
                EngineeringHealthHeight = 226
            });

            await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.InRange(writes, 1, 2);
            OperatorViewSettings loaded = store.Load();
            Assert.True(loaded.EngineeringHealthVisible);
            Assert.Equal(226, loaded.EngineeringHealthHeight);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dvmconsole-operator-view-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
