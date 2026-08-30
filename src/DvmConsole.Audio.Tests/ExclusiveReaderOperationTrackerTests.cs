using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class ExclusiveReaderOperationTrackerTests
{
    [Fact]
    public async Task RejectsOverlapAndLetsDisposalWaitForTheActiveOperation()
    {
        var tracker = new ExclusiveReaderOperationTracker();
        IDisposable operation = tracker.Begin("reader");

        Assert.Throws<InvalidOperationException>(() => tracker.Begin("reader"));

        Task idle = tracker.StopAccepting();
        Assert.False(idle.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => tracker.Begin("reader"));

        operation.Dispose();

        await idle.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
