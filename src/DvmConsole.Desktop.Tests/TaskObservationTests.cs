using DvmConsole.Threading;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class TaskObservationTests
{
    [Fact]
    public async Task Observe_ReportsBackgroundTaskFault()
    {
        var expected = new InvalidOperationException("background failure");
        var observed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskObservation.Observe(
            Task.FromException(expected),
            exception => observed.TrySetResult(exception));

        Assert.Same(expected, await observed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }
}
