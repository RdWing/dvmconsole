using System.Runtime.ExceptionServices;

namespace DvmConsole.Desktop;

// Runs independent cleanup steps to completion while retaining the original
// failures for the caller. This prevents one broken resource from leaking all
// resources that appear later in the ownership order.
internal sealed class AsyncCleanup
{
    private readonly List<Exception> failures = [];

    public void Run(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    public async Task RunTaskAsync(Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    public void Capture(Exception exception)
        => failures.Add(exception ?? throw new ArgumentNullException(nameof(exception)));

    public void ThrowIfFailed()
    {
        if (failures.Count == 0)
            return;
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("One or more cleanup operations failed.", failures);
    }
}
