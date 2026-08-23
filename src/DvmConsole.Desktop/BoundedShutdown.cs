namespace DvmConsole.Desktop;

internal static class BoundedShutdown
{
    public static async Task RunAsync(
        Func<Task> shutdownAsync,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shutdownAsync);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        Task shutdown = shutdownAsync();
        try
        {
            await shutdown.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Cleanup may still finish after the UI closes. Observe that task so
            // a late native-audio failure does not surface as an unobserved fault.
            TaskObservation.Observe(shutdown);
            throw new TimeoutException(
                $"Application cleanup did not finish within {timeout.TotalSeconds:0} seconds.");
        }
    }
}
