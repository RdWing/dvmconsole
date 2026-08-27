namespace DvmConsole.Media;

public readonly record struct AudioOutputPumpDiagnostics(
    long SignalRequests,
    long CoalescedSignalRequests,
    long SignaledWakeups,
    long TimeoutWakeups,
    long NoWorkWakeups,
    long FramesWritten,
    long MultiFrameWakeups)
{
    public long TotalWakeups => SignaledWakeups + TimeoutWakeups;
}

internal sealed class AudioOutputPumpDiagnosticsTracker
{
    private long signalRequests;
    private long coalescedSignalRequests;
    private long signaledWakeups;
    private long timeoutWakeups;
    private long noWorkWakeups;
    private long framesWritten;
    private long multiFrameWakeups;

    public void RecordSignalRequest() => Interlocked.Increment(ref signalRequests);

    public void RecordCoalescedSignalRequest()
        => Interlocked.Increment(ref coalescedSignalRequests);

    public void RecordWakeup(bool signaled)
    {
        if (signaled)
            Interlocked.Increment(ref signaledWakeups);
        else
            Interlocked.Increment(ref timeoutWakeups);
    }

    public void RecordFramesWritten(int count)
    {
        if (count == 0)
        {
            Interlocked.Increment(ref noWorkWakeups);
            return;
        }

        Interlocked.Add(ref framesWritten, count);
        if (count > 1)
            Interlocked.Increment(ref multiFrameWakeups);
    }

    public AudioOutputPumpDiagnostics Snapshot()
        => new(
            Interlocked.Read(ref signalRequests),
            Interlocked.Read(ref coalescedSignalRequests),
            Interlocked.Read(ref signaledWakeups),
            Interlocked.Read(ref timeoutWakeups),
            Interlocked.Read(ref noWorkWakeups),
            Interlocked.Read(ref framesWritten),
            Interlocked.Read(ref multiFrameWakeups));
}
