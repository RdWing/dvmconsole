using System.Diagnostics;

namespace DvmConsole.Threading;

// Owns the one intentional fire-and-forget boundary used by Desktop and Media.
// Every caller-supplied task is awaited here so faults cannot become unobserved.
internal static class TaskObservation
{
    public static void Observe(Task task, Action<Exception>? reportFault = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = ObserveAsync(task, reportFault);
    }

    private static async Task ObserveAsync(Task task, Action<Exception>? reportFault)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected completion state for background work.
        }
        catch (Exception exception)
        {
            try
            {
                if (reportFault is not null)
                    reportFault(exception);
                else
                    Trace.TraceError("Background task failed: {0}", exception);
            }
            catch (Exception reportingException)
            {
                Trace.TraceError(
                    "Background task fault reporting failed: {0}{1}Original fault: {2}",
                    reportingException,
                    Environment.NewLine,
                    exception);
            }
        }
    }
}
