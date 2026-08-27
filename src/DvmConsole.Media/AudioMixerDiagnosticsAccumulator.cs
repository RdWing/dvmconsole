namespace DvmConsole.Media;

internal sealed class AudioMixerDiagnosticsAccumulator
{
    private readonly Dictionary<string, MixerLaneDiagnosticsAccumulator> lanes = [];
    private long droppedSamples;
    private long overflowResynchronizations;
    private long protectedFrames;
    private long lowBufferRecoveries;
    private long latePumpWakes;
    private TimeSpan maximumPumpLateness;
    private int peakBufferedFrames;
    private string? lastDroppedLane;
    private long lastDroppedLaneSamples;
    private long gapFilledSamples;
    private long suppressedLiveConcealmentSamples;
    private long transitionDiscardedSamples;
    private long agedLiveSamples;

    public long DroppedSamples => droppedSamples;
    public long ProtectedFrames => protectedFrames;
    public long TransitionDiscardedSamples => transitionDiscardedSamples;

    public MixerLaneDiagnosticsAccumulator GetOrCreateLane(string label)
    {
        if (!lanes.TryGetValue(label, out MixerLaneDiagnosticsAccumulator? diagnostics))
        {
            diagnostics = new MixerLaneDiagnosticsAccumulator(label);
            lanes.Add(label, diagnostics);
        }
        return diagnostics;
    }

    public void AddTransitionDiscardedSamples(int count)
        => transitionDiscardedSamples += count;

    public void RecordLowBufferRecovery() => lowBufferRecoveries++;

    public void ObservePumpLateness(TimeSpan lateness)
    {
        latePumpWakes++;
        if (lateness > maximumPumpLateness)
            maximumPumpLateness = lateness;
    }

    public void RecordGap(MixerLaneBuffer lane, int sampleCount)
    {
        gapFilledSamples = checked(gapFilledSamples + sampleCount);
        lane.Diagnostics.GapFilledSamples = checked(
            lane.Diagnostics.GapFilledSamples + sampleCount);
    }

    public void RecordProtectedFrame() => protectedFrames++;

    public void RecordSuppressedConcealment(int sampleCount)
        => suppressedLiveConcealmentSamples += sampleCount;

    public void RecordOverflowResynchronization(MixerLaneBuffer lane)
    {
        overflowResynchronizations++;
        lane.Diagnostics.OverflowResynchronizations++;
    }

    public void ObserveBufferedFrames(MixerLaneBuffer lane)
    {
        peakBufferedFrames = Math.Max(peakBufferedFrames, lane.Frames.Count);
        lane.Diagnostics.PeakBufferedFrames = Math.Max(
            lane.Diagnostics.PeakBufferedFrames,
            lane.Frames.Count);
    }

    public void RecordDroppedSamples(MixerLaneBuffer lane, int sampleCount, bool aged)
    {
        lane.DroppedSamples = checked(lane.DroppedSamples + sampleCount);
        droppedSamples = checked(droppedSamples + sampleCount);
        lane.Diagnostics.DroppedSamples = checked(
            lane.Diagnostics.DroppedSamples + sampleCount);
        if (aged)
        {
            agedLiveSamples = checked(agedLiveSamples + sampleCount);
            lane.Diagnostics.AgedLiveSamples = checked(
                lane.Diagnostics.AgedLiveSamples + sampleCount);
        }
        lastDroppedLane = lane.DiagnosticLabel;
        lastDroppedLaneSamples = lane.DroppedSamples;
    }

    public AudioMixerDiagnostics Snapshot(
        int startupBufferedFrames,
        int maximumBufferedFrames,
        int targetOutputBufferedFrames,
        TimeSpan? physicalOutputStarvation,
        TimeSpan? pendingPhysicalOutputStarvation,
        long? physicalOutputCallbackCount,
        TimeSpan? physicalOutputCallbackAge,
        AudioOutputPumpDiagnostics outputPump)
        => new(
            droppedSamples,
            overflowResynchronizations,
            protectedFrames,
            lowBufferRecoveries,
            latePumpWakes,
            maximumPumpLateness,
            peakBufferedFrames,
            startupBufferedFrames,
            maximumBufferedFrames,
            targetOutputBufferedFrames,
            lastDroppedLane,
            lastDroppedLaneSamples,
            gapFilledSamples,
            suppressedLiveConcealmentSamples,
            transitionDiscardedSamples,
            physicalOutputStarvation,
            pendingPhysicalOutputStarvation,
            physicalOutputCallbackCount,
            physicalOutputCallbackAge,
            agedLiveSamples,
            lanes.Values
                .Select(diagnostics => diagnostics.Snapshot())
                .OrderByDescending(diagnostics => diagnostics.DroppedSamples)
                .ThenByDescending(diagnostics => diagnostics.GapFilledSamples)
                .ToArray(),
            outputPump);
}
