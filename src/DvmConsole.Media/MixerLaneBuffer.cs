namespace DvmConsole.Media;

internal sealed class MixerLaneBuffer(
    int id,
    int frameSamples,
    string diagnosticLabel,
    MixerLaneDiagnosticsAccumulator diagnostics)
{
    public int Id { get; } = id;
    public string DiagnosticLabel { get; } = diagnosticLabel;
    public MixerLaneDiagnosticsAccumulator Diagnostics { get; } = diagnostics;
    public Queue<short[]> Frames { get; } = [];
    public short[] PartialFrame { get; set; } = new short[frameSamples];
    public int PartialCount { get; set; }
    public double Gain { get; set; } = 1.0;
    public double Balance { get; set; }
    public int DroppedSamples { get; set; }
    public bool LivePlaybackEnabled { get; set; } = true;
    public bool PlayoutStarted { get; set; }
    public bool Completing { get; set; }
    public bool BoundarySmoothingPending { get; set; }
    public bool HasLastOutputSample { get; set; }
    public short LastOutputSample { get; set; }
    public long PresentedGapSamples { get; set; }
    public Action<ReadOnlyMemory<short>, TimeSpan>? PresentationObserver { get; set; }
    public Action<int, TimeSpan>? FrameHandedOff { get; set; }
    public long AcceptedSamples { get; set; }
    public long HandedOffSamples { get; set; }
    public long DrainedSamples { get; set; }
    public long PlaybackDrainTarget { get; set; }
    public TaskCompletionSource DrainCompletion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<TimeSpan>? PlaybackDrainCompletion { get; set; }
    public bool Disposed { get; set; }
}

internal sealed class MixerLaneDiagnosticsAccumulator(string label)
{
    public string Label { get; } = label;
    public long DroppedSamples { get; set; }
    public long OverflowResynchronizations { get; set; }
    public long GapFilledSamples { get; set; }
    public long AgedLiveSamples { get; set; }
    public int PeakBufferedFrames { get; set; }

    public AudioMixerLaneDiagnostics Snapshot()
        => new(
            Label,
            DroppedSamples,
            OverflowResynchronizations,
            GapFilledSamples,
            AgedLiveSamples,
            PeakBufferedFrames);
}
