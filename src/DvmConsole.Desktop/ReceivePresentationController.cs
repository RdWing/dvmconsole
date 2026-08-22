namespace DvmConsole.Desktop;

internal sealed class ReceivePresentationController
{
    private const int MaximumBatchSize = 64;
    private readonly object sync = new();
    private readonly Dictionary<SystemViewModel, SystemTrafficBuffer> pendingBySystem = [];
    private readonly HashSet<SystemViewModel> scheduledSystems = [];
    private readonly Func<bool> isDisposing;
    private readonly Func<bool> hasUiThreadAccess;
    private readonly Action<Action> postToUiThread;
    private readonly Action<SystemViewModel, SystemTrafficWorkItem, bool> present;

    public ReceivePresentationController(
        Func<bool> isDisposing,
        Func<bool> hasUiThreadAccess,
        Action<Action> postToUiThread,
        Action<SystemViewModel, SystemTrafficWorkItem, bool> present)
    {
        this.isDisposing = isDisposing ?? throw new ArgumentNullException(nameof(isDisposing));
        this.hasUiThreadAccess = hasUiThreadAccess ?? throw new ArgumentNullException(nameof(hasUiThreadAccess));
        this.postToUiThread = postToUiThread ?? throw new ArgumentNullException(nameof(postToUiThread));
        this.present = present ?? throw new ArgumentNullException(nameof(present));
    }

    public void Present(SystemViewModel system, SystemTrafficWorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (isDisposing())
            return;
        if (hasUiThreadAccess())
        {
            present(system, workItem, true);
            return;
        }

        bool schedule;
        lock (sync)
        {
            if (!pendingBySystem.TryGetValue(system, out SystemTrafficBuffer? pending))
            {
                pending = new SystemTrafficBuffer();
                pendingBySystem.Add(system, pending);
            }
            long droppedBefore = pending.DroppedCount;
            pending.Enqueue(workItem);
            system.RecordDroppedSystemTraffic(pending.DroppedCount - droppedBefore);
            schedule = scheduledSystems.Add(system);
        }

        if (schedule)
            postToUiThread(() => Drain(system));
    }

    private void Drain(SystemViewModel system)
    {
        if (isDisposing())
        {
            lock (sync)
            {
                pendingBySystem.Remove(system);
                scheduledSystems.Remove(system);
            }
            return;
        }

        int processed = 0;
        while (processed < MaximumBatchSize)
        {
            SystemTrafficWorkItem? workItem = null;
            bool empty;
            lock (sync)
            {
                empty = !pendingBySystem.TryGetValue(system, out SystemTrafficBuffer? pending) ||
                    !pending.TryDequeue(out workItem);
                if (empty)
                {
                    pendingBySystem.Remove(system);
                    scheduledSystems.Remove(system);
                }
            }

            if (empty)
                return;

            present(system, workItem!.Value, false);
            processed++;
        }

        postToUiThread(() => Drain(system));
    }
}
