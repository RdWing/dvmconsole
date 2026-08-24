using DvmConsole.Audio;
using System.Buffers;
using System.Diagnostics;

namespace DvmConsole.Media;

public sealed record AudioMixerDiagnostics(
    long DroppedSamples,
    long OverflowResynchronizations,
    long ProtectedFrames,
    long LowBufferRecoveries,
    long LatePumpWakes,
    TimeSpan MaximumPumpLateness,
    int PeakBufferedFrames,
    int StartupBufferedFrames,
    int MaximumBufferedFrames,
    int TargetOutputBufferedFrames,
    string? LastDroppedLane,
    long LastDroppedLaneSamples,
    long GapFilledSamples,
    long SuppressedLiveConcealmentSamples,
    long TransitionDiscardedSamples,
    TimeSpan? PhysicalOutputStarvation,
    TimeSpan? PendingPhysicalOutputStarvation = null,
    long? PhysicalOutputCallbackCount = null,
    TimeSpan? PhysicalOutputCallbackAge = null,
    long AgedLiveSamples = 0,
    IReadOnlyList<AudioMixerLaneDiagnostics>? LaneDiagnostics = null);

public sealed record AudioMixerLaneDiagnostics(
    string Label,
    long DroppedSamples,
    long OverflowResynchronizations,
    long GapFilledSamples,
    long AgedLiveSamples,
    int PeakBufferedFrames);

// Reports when a lane's PCM is handed to the physical output queue. The delay
// is the queued-device duration ahead of those samples, allowing presentation
// such as a channel meter to follow audible playout instead of decoder bursts.
public interface IAudioPlaybackPresentationSource
{
    void SetPresentationObserver(
        Action<ReadOnlyMemory<short>, TimeSpan>? observer);
}

// Lets a long-lived mixer lane pause its live clock between related network
// stream fragments without discarding buffered audio or resetting its playout
// history. This is intentionally separate from operator mute.
public interface IAudioPlaybackInputExpectationControl
{
    bool ExpectsMoreInput { get; set; }
}

// Marks a producer handoff inside one long-lived logical mixer lane. The next
// frame receives the same short click-suppression ramp used after a corrected
// live gap, without flushing queued audio or restarting the lane cushion.
public interface IAudioPlaybackBoundaryControl
{
    void MarkInputBoundary();
}

// Mixes PCM from independently selected receive channels into one playback
// stream. The mixer emits one 20 ms frame at a time and treats channels with
// no frame ready as silence, so a quiet channel cannot block an active one.
public sealed class AudioMixer : IAsyncDisposable
{
    // Four 20 ms frames keep the hardware just far enough ahead for stable
    // playout. A detected underrun temporarily adds two frames. Streaming
    // writes recover an overflowing lane to 80 ms; complete network-packet
    // writes retain the newest packet as one admission unit.
    private const int DefaultStartupBufferedFrames = 4;
    // P25 delivers nine 20 ms voice frames per LDU. Bound the lane at exactly
    // three complete LDUs so two-LDU steady state retains one full LDU of
    // jitter headroom without splitting the emergency limit across a packet.
    // The physical output target remains 80 ms.
    private const int MaximumBufferedFrames = 27;
    private const int OverflowRecoveryFrames = 4;
    private const int BoundarySmoothingSamples = 40;
    private const int NormalOutputBufferedFrames = 4;
    private const int RecoveryOutputBufferedFrames = 6;
    private const int LiveConcealmentBufferedFrames = 9;
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan RecoveryHoldDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OutputCallbackStallTimeout = TimeSpan.FromSeconds(1);
    private readonly IAudioPlayback output;
    private readonly PcmAudioFormat inputFormat;
    private readonly object sync = new();
    private readonly Dictionary<int, MixerLaneBuffer> channels = [];
    private readonly AudioMixerDiagnosticsAccumulator diagnostics = new();
    private readonly AudioOutputPump outputPump;
    private readonly int frameSamples;
    private readonly int outputFrameSamples;
    private readonly int startupBufferedFrames;
    private readonly int maximumBufferedSamples;
    private readonly double[] leftMix;
    private readonly double[]? rightMix;
    private readonly short[] outputFrame;
    private readonly short[] silentInputFrame;
    private readonly bool supportsBufferedPlayout;
    private readonly IAudioPlaybackCallbackDiagnostics? callbackDiagnostics;
    private readonly IPhysicalAudioOutputDiagnosticsSource? routedOutputDiagnostics;
    private int nextChannelId;
    private int targetOutputBufferedFrames = NormalOutputBufferedFrames;
    private long recoveryHoldUntilTimestamp;
    private bool outputWasPrimed;
    private bool inputDiscarded;
    private bool stopping;
    private bool draining;
    private bool disposed;
    private TaskCompletionSource? disposeCompletion;
    private Exception? failure;
    private long lastOutputCallbackCount;
    private long lastOutputCallbackTimestamp;

    public AudioMixer(IAudioPlayback output)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        if (output.Format.Channels is not (1 or 2) || output.Format.BitsPerSample != 16)
            throw new NotSupportedException("Audio mixing supports mono or stereo 16-bit PCM output only.");

        frameSamples = Math.Max(1, output.Format.SampleRate / 50);
        outputFrameSamples = checked(frameSamples * output.Format.Channels);
        supportsBufferedPlayout = output.QueuedSamples is not null;
        callbackDiagnostics = output as IAudioPlaybackCallbackDiagnostics;
        routedOutputDiagnostics = output as IPhysicalAudioOutputDiagnosticsSource;
        lastOutputCallbackCount = ReadPhysicalOutputDiagnostics().OutputCallbackCount ?? 0;
        lastOutputCallbackTimestamp = Stopwatch.GetTimestamp();
        startupBufferedFrames = !supportsBufferedPlayout
            ? 1
            : DefaultStartupBufferedFrames;
        inputFormat = new PcmAudioFormat(output.Format.SampleRate, 1, output.Format.BitsPerSample);
        maximumBufferedSamples = checked(frameSamples * MaximumBufferedFrames);
        leftMix = new double[frameSamples];
        rightMix = output.Format.Channels == 2 ? new double[frameSamples] : null;
        outputFrame = new short[outputFrameSamples];
        silentInputFrame = new short[frameSamples];
        outputPump = new AudioOutputPump(
            output,
            PumpInterval,
            FramesNeededForOutputBuffer,
            GetPhysicalQueueDuration,
            ShouldCoalesceFirstFrame,
            TryTakeFrame,
            MarkOutputPrimed,
            ObservePumpLateness,
            RecordPumpFailure);
    }

    public PcmAudioFormat Format => inputFormat;

    public event Action<Exception>? Faulted;

    public int MaximumBufferedSamples => maximumBufferedSamples;

    public long DroppedSamples
    {
        get
        {
            lock (sync)
                return diagnostics.DroppedSamples;
        }
    }

    public long ProtectedFrames
    {
        get
        {
            lock (sync)
                return diagnostics.ProtectedFrames;
        }
    }

    public AudioMixerDiagnostics GetDiagnostics()
    {
        PhysicalAudioOutputDiagnostics physical = ReadPhysicalOutputDiagnostics();
        lock (sync)
        {
            TimeSpan? outputCallbackAge = physical.OutputCallbackCount is null
                ? null
                : Stopwatch.GetElapsedTime(lastOutputCallbackTimestamp);
            return diagnostics.Snapshot(
                startupBufferedFrames,
                MaximumBufferedFrames,
                targetOutputBufferedFrames,
                physical.StarvedDuration,
                physical.PendingStarvedDuration,
                physical.OutputCallbackCount,
                outputCallbackAge);
        }
    }

    // Used while live output is intentionally suppressed, including cold
    // Bluetooth profile transitions and operator mute. Decoder/TAR observation
    // continues, and live PCM is not accumulated for delayed playback.
    public long SetInputDiscarded(bool discarded)
    {
        bool endExpectedPlayback = false;
        long discardedSamples;
        lock (sync)
        {
            ThrowIfUnavailable();
            if (inputDiscarded == discarded)
                return diagnostics.TransitionDiscardedSamples;

            inputDiscarded = discarded;
            if (!discarded)
                return diagnostics.TransitionDiscardedSamples;

            endExpectedPlayback = outputWasPrimed;
            outputWasPrimed = false;
            targetOutputBufferedFrames = NormalOutputBufferedFrames;
            recoveryHoldUntilTimestamp = 0;

            List<MixerLaneBuffer>? drainedChannels = null;
            foreach (MixerLaneBuffer channel in channels.Values)
            {
                while (channel.Frames.TryDequeue(out short[]? frame))
                    diagnostics.AddTransitionDiscardedSamples(frame.Length);
                diagnostics.AddTransitionDiscardedSamples(channel.PartialCount);
                channel.PartialCount = 0;
                channel.PlayoutStarted = false;
                channel.BoundarySmoothingPending = false;
                channel.PresentedGapSamples = 0;
                channel.HandedOffSamples = channel.AcceptedSamples;
                channel.DrainedSamples = channel.AcceptedSamples;
                channel.PlaybackDrainCompletion?.TrySetResult(TimeSpan.Zero);
                channel.PlaybackDrainCompletion = null;
                if (channel.Completing)
                {
                    drainedChannels ??= [];
                    drainedChannels.Add(channel);
                }
            }
            if (drainedChannels is not null)
            {
                foreach (MixerLaneBuffer channel in drainedChannels)
                    RemoveDrainedChannelLocked(channel);
            }
            discardedSamples = diagnostics.TransitionDiscardedSamples;
        }

        // Intentional output suppression is not a playback fault.
        // Close the current continuity window so native starvation diagnostics
        // do not count the deliberately discarded interval as an underrun.
        if (endExpectedPlayback && output is IAudioPlaybackContinuityDiagnostics continuity)
            continuity.EndExpectedPlayback();
        return discardedSamples;
    }

    public IAudioPlayback OpenChannel(string? diagnosticLabel = null)
    {
        lock (sync)
        {
            ThrowIfUnavailable();
            int id = ++nextChannelId;
            string label = string.IsNullOrWhiteSpace(diagnosticLabel)
                ? $"mixer lane {id}"
                : diagnosticLabel.Trim();
            MixerLaneDiagnosticsAccumulator laneDiagnostics = diagnostics.GetOrCreateLane(label);
            var channel = new MixerLaneBuffer(id, frameSamples, label, laneDiagnostics);
            channel.FrameHandedOff = (sampleCount, presentationDelay) =>
                MarkFrameHandedOff(channel, sampleCount, presentationDelay);
            channels.Add(channel.Id, channel);
            return new ChannelPlayback(this, channel);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        bool startDisposal = false;
        lock (sync)
        {
            if (disposeCompletion is null)
            {
                disposeCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                startDisposal = true;
            }
            completion = disposeCompletion;
        }

        if (startDisposal)
            TaskObservation.Observe(DisposeAndCompleteAsync(completion));
        return new ValueTask(completion.Task);
    }

    private async Task DisposeAndCompleteAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task[] channelDrains;
        lock (sync)
        {
            stopping = true;
            draining = true;
            MixerLaneBuffer[] activeChannels = channels.Values.ToArray();
            foreach (MixerLaneBuffer channel in activeChannels)
                CompleteChannelLocked(channel);
            channelDrains = activeChannels
                .Select(channel => channel.DrainCompletion.Task)
                .ToArray();
            SignalDataAvailable();
        }

        try
        {
            await Task.WhenAll(channelDrains).ConfigureAwait(false);
            await output.DrainAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                disposed = true;
                foreach (MixerLaneBuffer channel in channels.Values)
                {
                    channel.Disposed = true;
                    channel.DrainCompletion.TrySetResult();
                }
                channels.Clear();
                outputPump.Cancel();
                SignalDataAvailable();
            }

            try
            {
                await outputPump.Completion.ConfigureAwait(false);
            }
            finally
            {
                outputPump.Dispose();
                await output.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private int FramesNeededForOutputBuffer()
    {
        lock (sync)
        {
            if (draining)
                return HasReadyFramesLocked() ? 1 : 0;
        }

        if (output.QueuedSamples is not int queuedSamples)
        {
            bool unbufferedPlaybackExpected;
            lock (sync)
            {
                unbufferedPlaybackExpected = HasReadyFramesLocked() ||
                    ExpectsMoreLiveInputLocked() ||
                    outputWasPrimed;
            }
            ObserveOutputCallbackHealth(unbufferedPlaybackExpected);
            return 1;
        }

        long now = Stopwatch.GetTimestamp();
        int targetFrames;
        bool endExpectedPlayback = false;
        bool expectsPlayback;
        lock (sync)
        {
            bool hasReadyFrames = HasReadyFramesLocked();
            bool expectsMoreInput = ExpectsMoreLiveInputLocked();
            expectsPlayback = hasReadyFrames ||
                expectsMoreInput ||
                (outputWasPrimed && queuedSamples > 0);
            if (!hasReadyFrames && queuedSamples == 0 && !expectsMoreInput)
            {
                endExpectedPlayback = outputWasPrimed;
                outputWasPrimed = false;
                targetOutputBufferedFrames = NormalOutputBufferedFrames;
                recoveryHoldUntilTimestamp = 0;
            }
            if (hasReadyFrames && outputWasPrimed && queuedSamples < outputFrameSamples)
            {
                diagnostics.RecordLowBufferRecovery();
                targetOutputBufferedFrames = RecoveryOutputBufferedFrames;
                recoveryHoldUntilTimestamp = now + (long)(RecoveryHoldDuration.TotalSeconds * Stopwatch.Frequency);
            }
            else if (targetOutputBufferedFrames == RecoveryOutputBufferedFrames &&
                     now >= recoveryHoldUntilTimestamp)
            {
                targetOutputBufferedFrames = NormalOutputBufferedFrames;
            }
            targetFrames = targetOutputBufferedFrames;
        }
        ObserveOutputCallbackHealth(expectsPlayback);
        if (endExpectedPlayback && output is IAudioPlaybackContinuityDiagnostics continuity)
            continuity.EndExpectedPlayback();

        // Use the device's actual queue depth to refill several already-decoded
        // frames after a delayed wake instead of remaining behind.
        int targetOutputBufferedSamples = checked(outputFrameSamples * targetFrames);
        int deficit = targetOutputBufferedSamples - Math.Max(0, queuedSamples);
        if (deficit <= 0)
            return 0;
        return Math.Min(
            targetFrames,
            (deficit + outputFrameSamples - 1) / outputFrameSamples);
    }

    private TimeSpan GetPhysicalQueueDuration()
    {
        int queuedSamples = Math.Max(0, output.QueuedSamples ?? 0);
        double samplesPerSecond = checked(
            output.Format.SampleRate * output.Format.Channels);
        return TimeSpan.FromSeconds(queuedSamples / samplesPerSecond);
    }

    private bool ShouldCoalesceFirstFrame()
    {
        lock (sync)
            return !outputWasPrimed;
    }

    private void MarkOutputPrimed()
    {
        lock (sync)
            outputWasPrimed = true;
    }

    private void ObservePumpLateness(TimeSpan lateness)
    {
        lock (sync)
        {
            if (lateness >= PumpInterval && CanProduceFrameLocked())
            {
                diagnostics.ObservePumpLateness(lateness);
            }
        }
    }

    private void RecordPumpFailure(Exception exception)
    {
        Action<Exception>? handler;
        lock (sync)
        {
            if (failure is not null)
                return;
            failure = exception;
            foreach (MixerLaneBuffer channel in channels.Values)
            {
                channel.DrainCompletion.TrySetException(exception);
                TaskObservation.Observe(channel.DrainCompletion.Task);
                channel.PlaybackDrainCompletion?.TrySetException(exception);
                if (channel.PlaybackDrainCompletion is not null)
                    TaskObservation.Observe(channel.PlaybackDrainCompletion.Task);
            }
            handler = Faulted;
        }

        try
        {
            handler?.Invoke(exception);
        }
        catch
        {
            // A diagnostic observer must not terminate the audio pump.
        }
    }

    private bool TryTakeFrame(
        out ReadOnlyMemory<short> frame,
        out MixerPresentationNotification[] notifications,
        out int notificationCount)
    {
        lock (sync)
        {
            if (!CanProduceFrameLocked())
            {
                frame = default;
                notifications = [];
                notificationCount = 0;
                return false;
            }

            PcmMixKernel.Clear(leftMix, rightMix);
            List<MixerLaneBuffer>? drainedChannels = null;
            MixerPresentationNotification[]? presented = null;
            int presentedCount = 0;
            foreach (MixerLaneBuffer channel in channels.Values)
            {
                if (!channel.PlayoutStarted)
                    continue;

                if (!channel.Frames.TryDequeue(out short[]? source))
                {
                    if (!supportsBufferedPlayout || channel.Completing || !channel.LivePlaybackEnabled)
                        continue;

                    channel.PresentedGapSamples = checked(
                        channel.PresentedGapSamples + frameSamples);
                    diagnostics.RecordGap(channel, frameSamples);
                    channel.LastOutputSample = 0;
                    channel.HasLastOutputSample = true;
                    channel.BoundarySmoothingPending = true;
                    if (channel.PresentationObserver is not null)
                    {
                        presented ??= ArrayPool<MixerPresentationNotification>.Shared.Rent(channels.Count);
                        presented[presentedCount++] = new MixerPresentationNotification(
                            null,
                            channel.PresentationObserver,
                            silentInputFrame);
                    }
                    continue;
                }

                SmoothCorrectedBoundary(channel, source);

                presented ??= ArrayPool<MixerPresentationNotification>.Shared.Rent(channels.Count);
                presented[presentedCount++] = new MixerPresentationNotification(
                    channel,
                    channel.PresentationObserver,
                    source);

                int count = PcmMixKernel.Accumulate(
                    source,
                    channel.Gain,
                    channel.Balance,
                    leftMix,
                    rightMix);

                if (count > 0)
                {
                    channel.LastOutputSample = source[count - 1];
                    channel.HasLastOutputSample = true;
                }
                if (channel.Completing && channel.Frames.Count == 0 && channel.PartialCount == 0)
                {
                    drainedChannels ??= [];
                    drainedChannels.Add(channel);
                }
            }

            if (drainedChannels is not null)
            {
                foreach (MixerLaneBuffer channel in drainedChannels)
                    RemoveDrainedChannelLocked(channel);
            }

            if (PcmMixKernel.Render(
                    leftMix,
                    rightMix,
                    output.Format.Channels,
                    outputFrame))
            {
                diagnostics.RecordProtectedFrame();
            }
            frame = outputFrame;
            notifications = presented ?? [];
            notificationCount = presentedCount;
            return true;
        }
    }

    private bool HasReadyFramesLocked()
    {
        foreach (MixerLaneBuffer channel in channels.Values)
        {
            if (channel.PlayoutStarted && channel.Frames.Count > 0)
                return true;
        }
        return false;
    }

    private bool CanProduceFrameLocked()
        => HasReadyFramesLocked() ||
           (supportsBufferedPlayout && ExpectsMoreLiveInputLocked());

    private bool ExpectsMoreLiveInputLocked()
        => channels.Values.Any(channel =>
            channel.LivePlaybackEnabled &&
            channel.ExpectsMoreInput &&
            channel.PlayoutStarted &&
            !channel.Completing);

    private void Enqueue(
        MixerLaneBuffer channel,
        ReadOnlyMemory<short> samples,
        bool concealment,
        bool packetAdmission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (samples.IsEmpty)
            return;

        lock (sync)
        {
            ThrowIfUnavailable();
            if (channel.Disposed || !channels.ContainsKey(channel.Id))
                throw new ObjectDisposedException(nameof(IAudioPlayback));
            if (!channel.LivePlaybackEnabled)
                return;
            if (inputDiscarded)
            {
                diagnostics.AddTransitionDiscardedSamples(samples.Length);
                return;
            }

            if (concealment)
            {
                int alreadyPresented = (int)Math.Min(
                    channel.PresentedGapSamples,
                    samples.Length);
                if (alreadyPresented > 0)
                {
                    channel.PresentedGapSamples -= alreadyPresented;
                    diagnostics.RecordSuppressedConcealment(alreadyPresented);
                    samples = samples[alreadyPresented..];
                    if (samples.IsEmpty)
                        return;
                }

                int bufferedSamples = checked(
                    channel.Frames.Count * frameSamples + channel.PartialCount);
                int availableSamples = Math.Max(
                    0,
                    LiveConcealmentBufferedFrames * frameSamples - bufferedSamples);
                if (samples.Length > availableSamples)
                {
                    int suppressedSamples = samples.Length - availableSamples;
                    diagnostics.RecordSuppressedConcealment(suppressedSamples);
                    if (availableSamples == 0)
                        return;

                    // Keep the most recent replacement audio so the live lane
                    // rejoins the current packet instead of replaying the
                    // oldest part of a long recovered gap.
                    samples = samples[^availableSamples..];
                }
            }
            else if (channel.PresentedGapSamples > 0)
            {
                // A packet can resume without declaring packet loss. Its PCM
                // is current input, so only explicit replacement audio may pay
                // back a gap that the live clock already presented as silence.
                channel.PresentedGapSamples = 0;
                channel.BoundarySmoothingPending = channel.HasLastOutputSample;
            }

            if (packetAdmission)
                AgePacketBacklogLocked(channel, samples.Length);

            channel.AcceptedSamples = checked(channel.AcceptedSamples + samples.Length);

            ReadOnlySpan<short> incoming = samples.Span;
            while (!incoming.IsEmpty)
            {
                int count = Math.Min(frameSamples - channel.PartialCount, incoming.Length);
                incoming[..count].CopyTo(channel.PartialFrame.AsSpan(channel.PartialCount));
                channel.PartialCount += count;
                incoming = incoming[count..];
                if (channel.PartialCount < frameSamples)
                    continue;

                short[] completedFrame = channel.PartialFrame;
                channel.PartialFrame = new short[frameSamples];
                channel.PartialCount = 0;
                QueueFrameLocked(channel, completedFrame);
            }
        }
    }

    private void QueueFrameLocked(MixerLaneBuffer channel, short[] frame)
    {
        bool overflowCorrected = false;
        if (channel.Frames.Count >= MaximumBufferedFrames)
        {
            while (channel.Frames.Count > OverflowRecoveryFrames &&
                   channel.Frames.TryDequeue(out short[]? discarded))
            {
                overflowCorrected = true;
                RecordDroppedFrameLocked(channel, discarded.Length, aged: false);
                channel.HandedOffSamples = checked(channel.HandedOffSamples + discarded.Length);
            }
        }
        if (overflowCorrected)
        {
            diagnostics.RecordOverflowResynchronization(channel);
            channel.BoundarySmoothingPending = channel.HasLastOutputSample;
            CompletePlaybackDrainIfSatisfiedLocked(channel, TimeSpan.Zero);
        }

        channel.Frames.Enqueue(frame);
        if (!channel.PlayoutStarted &&
            (channel.Completing || channel.Frames.Count >= startupBufferedFrames))
        {
            channel.PlayoutStarted = true;
        }
        diagnostics.ObserveBufferedFrames(channel);
        SignalDataAvailable();
    }

    private void AgePacketBacklogLocked(MixerLaneBuffer channel, int incomingSamples)
    {
        int incomingFrames = checked(
            (channel.PartialCount + incomingSamples) / frameSamples);
        if (incomingFrames <= 0)
            return;

        int projectedFrames = checked(channel.Frames.Count + incomingFrames);
        if (projectedFrames <= MaximumBufferedFrames)
            return;

        int droppedFrames = 0;
        while (channel.Frames.TryDequeue(out short[]? discarded))
        {
            RecordDroppedFrameLocked(channel, discarded.Length, aged: true);
            channel.HandedOffSamples = checked(channel.HandedOffSamples + discarded.Length);
            droppedFrames++;
        }
        int droppedPartialSamples = channel.PartialCount;
        if (droppedPartialSamples > 0)
        {
            RecordDroppedFrameLocked(channel, droppedPartialSamples, aged: true);
            channel.HandedOffSamples = checked(
                channel.HandedOffSamples + droppedPartialSamples);
            channel.PartialCount = 0;
        }
        if (droppedFrames == 0 && droppedPartialSamples == 0)
            return;

        diagnostics.RecordOverflowResynchronization(channel);
        channel.BoundarySmoothingPending = channel.HasLastOutputSample;
        CompletePlaybackDrainIfSatisfiedLocked(channel, TimeSpan.Zero);
    }

    private void RecordDroppedFrameLocked(
        MixerLaneBuffer channel,
        int sampleCount,
        bool aged)
    {
        diagnostics.RecordDroppedSamples(channel, sampleCount, aged);
    }

    private void ObserveOutputCallbackHealth(bool expectsPlayback)
    {
        long? observedCallbackCount = ReadPhysicalOutputDiagnostics().OutputCallbackCount;
        if (observedCallbackCount is null)
            return;

        long now = Stopwatch.GetTimestamp();
        long callbackCount = observedCallbackCount.Value;
        lock (sync)
        {
            if (callbackCount != lastOutputCallbackCount)
            {
                lastOutputCallbackCount = callbackCount;
                lastOutputCallbackTimestamp = now;
                return;
            }

            if (!outputWasPrimed || !expectsPlayback)
            {
                lastOutputCallbackTimestamp = now;
                return;
            }

            if (Stopwatch.GetElapsedTime(lastOutputCallbackTimestamp, now) >=
                OutputCallbackStallTimeout)
            {
                throw new IOException(
                    "The physical audio output callback stopped advancing while live playback was active.");
            }
        }
    }

    private PhysicalAudioOutputDiagnostics ReadPhysicalOutputDiagnostics()
    {
        if (routedOutputDiagnostics is not null)
            return routedOutputDiagnostics.GetPhysicalOutputDiagnostics();

        IAudioPlaybackContinuityDiagnostics? continuity =
            output as IAudioPlaybackContinuityDiagnostics;
        return new PhysicalAudioOutputDiagnostics(
            continuity?.StarvedDuration,
            continuity?.PendingStarvedDuration,
            callbackDiagnostics?.OutputCallbackCount);
    }

    private static void SmoothCorrectedBoundary(MixerLaneBuffer channel, short[] source)
    {
        if (!channel.BoundarySmoothingPending || !channel.HasLastOutputSample || source.Length == 0)
            return;

        int count = Math.Min(BoundarySmoothingSamples, source.Length);
        double start = channel.LastOutputSample;
        for (int index = 0; index < count; index++)
        {
            double progress = (index + 1.0) / count;
            source[index] = PcmMixKernel.ToPcm(start + ((source[index] - start) * progress));
        }
        channel.BoundarySmoothingPending = false;
    }

    private void Complete(MixerLaneBuffer channel)
    {
        lock (sync)
        {
            if (channel.Disposed)
                return;
            CompleteChannelLocked(channel);
        }
    }

    private bool GetLivePlaybackEnabled(MixerLaneBuffer channel)
    {
        lock (sync)
            return channel.LivePlaybackEnabled;
    }

    private bool GetExpectsMoreInput(MixerLaneBuffer channel)
    {
        lock (sync)
            return channel.ExpectsMoreInput;
    }

    private void SetExpectsMoreInput(MixerLaneBuffer channel, bool expected)
    {
        lock (sync)
        {
            ThrowIfUnavailable();
            if (channel.Disposed || !channels.ContainsKey(channel.Id))
                throw new ObjectDisposedException(nameof(IAudioPlayback));
            channel.ExpectsMoreInput = expected;
            if (expected)
                SignalDataAvailable();
        }
    }

    private void MarkInputBoundary(MixerLaneBuffer channel)
    {
        lock (sync)
        {
            ThrowIfUnavailable();
            if (channel.Disposed || !channels.ContainsKey(channel.Id))
                throw new ObjectDisposedException(nameof(IAudioPlayback));
            channel.BoundarySmoothingPending = channel.HasLastOutputSample;
        }
    }

    private void SetLivePlaybackEnabled(MixerLaneBuffer channel, bool enabled)
    {
        lock (sync)
        {
            ThrowIfUnavailable();
            if (channel.Disposed || !channels.ContainsKey(channel.Id))
                throw new ObjectDisposedException(nameof(IAudioPlayback));
            if (channel.LivePlaybackEnabled == enabled)
                return;

            channel.LivePlaybackEnabled = enabled;
            if (enabled)
                return;

            channel.Frames.Clear();
            channel.PartialCount = 0;
            channel.HandedOffSamples = channel.AcceptedSamples;
            channel.DrainedSamples = channel.AcceptedSamples;
            channel.PlayoutStarted = false;
            channel.BoundarySmoothingPending = false;
            channel.HasLastOutputSample = false;
            channel.PresentedGapSamples = 0;
            channel.HandedOffSamples = channel.AcceptedSamples;
            channel.DrainedSamples = channel.AcceptedSamples;
            channel.PlaybackDrainCompletion?.TrySetResult(TimeSpan.Zero);
            channel.PlaybackDrainCompletion = null;
        }
    }

    private void Remove(MixerLaneBuffer channel)
    {
        lock (sync)
        {
            if (channel.Disposed)
                return;

            channel.Frames.Clear();
            channel.PartialCount = 0;
            channel.PlaybackDrainCompletion?.TrySetException(
                new ObjectDisposedException(nameof(IAudioPlayback)));
            channel.PlaybackDrainCompletion = null;
            RemoveDrainedChannelLocked(channel);
        }
    }

    private async ValueTask<int?> DrainChannelAsync(
        MixerLaneBuffer channel,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<TimeSpan>? completion;
        int queuedSamples;
        lock (sync)
        {
            ThrowIfUnavailable();
            if (channel.Disposed || !channels.ContainsKey(channel.Id))
                throw new ObjectDisposedException(nameof(IAudioPlayback));
            if (channel.PlaybackDrainCompletion is not null)
                throw new InvalidOperationException("This mixer lane is already draining.");

            long drainTarget = channel.AcceptedSamples;
            queuedSamples = checked((int)Math.Min(
                int.MaxValue,
                Math.Max(0, drainTarget - channel.DrainedSamples)));
            channel.DrainedSamples = drainTarget;
            if (channel.PartialCount > 0)
            {
                short[] completedFrame = channel.PartialFrame;
                Array.Clear(completedFrame, channel.PartialCount, completedFrame.Length - channel.PartialCount);
                channel.PartialFrame = new short[frameSamples];
                channel.PartialCount = 0;
                QueueFrameLocked(channel, completedFrame);
            }
            if (channel.HandedOffSamples >= drainTarget)
                return queuedSamples;

            completion = new TaskCompletionSource<TimeSpan>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            channel.PlaybackDrainCompletion = completion;
            channel.PlaybackDrainTarget = drainTarget;
            channel.PlayoutStarted = true;
            SignalDataAvailable();
        }

        TimeSpan presentationDelay;
        try
        {
            presentationDelay = await completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (sync)
            {
                if (ReferenceEquals(channel.PlaybackDrainCompletion, completion))
                    channel.PlaybackDrainCompletion = null;
            }
            throw;
        }
        if (presentationDelay > TimeSpan.Zero)
            await Task.Delay(presentationDelay, cancellationToken).ConfigureAwait(false);
        return queuedSamples;
    }

    private void MarkFrameHandedOff(
        MixerLaneBuffer channel,
        int sampleCount,
        TimeSpan presentationDelay)
    {
        lock (sync)
        {
            channel.HandedOffSamples = checked(channel.HandedOffSamples + sampleCount);
            CompletePlaybackDrainIfSatisfiedLocked(
                channel,
                presentationDelay + TimeSpan.FromMilliseconds(20));
        }
    }

    private static void CompletePlaybackDrainIfSatisfiedLocked(
        MixerLaneBuffer channel,
        TimeSpan presentationDelay)
    {
        if (channel.PlaybackDrainCompletion is null ||
            channel.HandedOffSamples < channel.PlaybackDrainTarget)
        {
            return;
        }

        TaskCompletionSource<TimeSpan> completion = channel.PlaybackDrainCompletion;
        channel.PlaybackDrainCompletion = null;
        completion.TrySetResult(presentationDelay);
    }

    private void CompleteChannelLocked(MixerLaneBuffer channel)
    {
        if (channel.Disposed || channel.Completing)
            return;

        channel.Completing = true;
        if (channel.PartialCount > 0)
        {
            short[] completedFrame = channel.PartialFrame;
            Array.Clear(completedFrame, channel.PartialCount, completedFrame.Length - channel.PartialCount);
            channel.PartialFrame = new short[frameSamples];
            channel.PartialCount = 0;
            QueueFrameLocked(channel, completedFrame);
        }
        channel.PlayoutStarted = true;
        if (channel.Frames.Count == 0)
            RemoveDrainedChannelLocked(channel);
        else
            SignalDataAvailable();
    }

    private void RemoveDrainedChannelLocked(MixerLaneBuffer channel)
    {
        channel.Disposed = true;
        channels.Remove(channel.Id);
        channel.HandedOffSamples = channel.AcceptedSamples;
        channel.DrainedSamples = channel.AcceptedSamples;
        channel.PlaybackDrainCompletion?.TrySetResult(TimeSpan.Zero);
        channel.PlaybackDrainCompletion = null;
        channel.DrainCompletion.TrySetResult();
    }

    private void SignalDataAvailable()
        => outputPump.Signal();

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(disposed || stopping, this);
        if (failure is not null)
            throw new IOException("The shared audio mixer stopped.", failure);
    }

    private sealed class ChannelPlayback(AudioMixer owner, MixerLaneBuffer channel) :
        IAudioPlayback,
        IConcealmentAudioPlayback,
        ILivePacketAudioPlayback,
        ILiveAudioPlaybackControl,
        IAudioPlaybackPresentationSource,
        IAudioGainControl,
        IAudioBalanceControl,
        IAudioPlaybackInputExpectationControl,
        IAudioPlaybackBoundaryControl,
        IPhysicalAudioOutputDiagnosticsSource
    {
        private bool disposed;

        public PcmAudioFormat Format => owner.Format;

        public PhysicalAudioOutputDiagnostics GetPhysicalOutputDiagnostics()
        {
            AudioMixerDiagnostics current = owner.GetDiagnostics();
            return new PhysicalAudioOutputDiagnostics(
                current.PhysicalOutputStarvation,
                current.PendingPhysicalOutputStarvation,
                current.PhysicalOutputCallbackCount);
        }

        public bool LivePlaybackEnabled
        {
            get => owner.GetLivePlaybackEnabled(channel);
            set
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                owner.SetLivePlaybackEnabled(channel, value);
            }
        }

        public bool ExpectsMoreInput
        {
            get => owner.GetExpectsMoreInput(channel);
            set
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                owner.SetExpectsMoreInput(channel, value);
            }
        }

        public void MarkInputBoundary()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.MarkInputBoundary(channel);
        }

        public double Gain
        {
            get
            {
                lock (owner.sync)
                    return channel.Gain;
            }
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value is < 0 or > 4)
                    throw new ArgumentOutOfRangeException(nameof(value), "Audio gain must be between 0 and 4.");
                lock (owner.sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    owner.ThrowIfUnavailable();
                    if (channel.Disposed || !owner.channels.ContainsKey(channel.Id))
                        throw new ObjectDisposedException(nameof(IAudioPlayback));
                    channel.Gain = value;
                }
            }
        }

        public double Balance
        {
            get
            {
                lock (owner.sync)
                    return channel.Balance;
            }
            set
            {
                if (!double.IsFinite(value) || value is < -1 or > 1)
                    throw new ArgumentOutOfRangeException(nameof(value), "Audio balance must be between -1 and 1.");
                lock (owner.sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    owner.ThrowIfUnavailable();
                    if (channel.Disposed || !owner.channels.ContainsKey(channel.Id))
                        throw new ObjectDisposedException(nameof(IAudioPlayback));
                    channel.Balance = value;
                }
            }
        }

        public void SetPresentationObserver(
            Action<ReadOnlyMemory<short>, TimeSpan>? observer)
        {
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                owner.ThrowIfUnavailable();
                if (channel.Disposed || !owner.channels.ContainsKey(channel.Id))
                    throw new ObjectDisposedException(nameof(IAudioPlayback));
                channel.PresentationObserver = observer;
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Enqueue(
                channel,
                samples,
                concealment: false,
                packetAdmission: false,
                cancellationToken);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteLivePacketAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Enqueue(
                channel,
                samples,
                concealment: false,
                packetAdmission: true,
                cancellationToken);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteConcealmentAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Enqueue(
                channel,
                samples,
                concealment: true,
                packetAdmission: false,
                cancellationToken);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Complete(channel);
            return ValueTask.CompletedTask;
        }

        public ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return owner.DrainChannelAsync(channel, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                owner.Remove(channel);
            }

            return ValueTask.CompletedTask;
        }
    }

}
