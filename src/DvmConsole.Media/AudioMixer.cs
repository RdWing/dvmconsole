using DvmConsole.Audio;
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
    TimeSpan? PhysicalOutputStarvation);

// Reports when a lane's PCM is handed to the physical output queue. The delay
// is the queued-device duration ahead of those samples, allowing presentation
// such as a channel meter to follow audible playout instead of decoder bursts.
public interface IAudioPlaybackPresentationSource
{
    void SetPresentationObserver(
        Action<ReadOnlyMemory<short>, TimeSpan>? observer);
}

// Mixes PCM from independently selected receive channels into one playback
// stream. The mixer emits one 20 ms frame at a time and treats channels with
// no frame ready as silence, so a quiet channel cannot block an active one.
public sealed class AudioMixer : IAsyncDisposable
{
    // Four 20 ms frames keep the hardware just far enough ahead for stable
    // playout. A detected underrun temporarily adds two frames, while a lane
    // that reaches 320 ms is collapsed back to the normal 80 ms depth.
    private const int DefaultStartupBufferedFrames = 4;
    private const int MaximumBufferedFrames = 16;
    private const int OverflowRecoveryFrames = 4;
    private const int BoundarySmoothingSamples = 40;
    private const int NormalOutputBufferedFrames = 4;
    private const int RecoveryOutputBufferedFrames = 6;
    private const int LiveConcealmentBufferedFrames = 9;
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan RecoveryHoldDuration = TimeSpan.FromSeconds(5);
    private readonly IAudioPlayback output;
    private readonly PcmAudioFormat inputFormat;
    private readonly object sync = new();
    private readonly Dictionary<int, ChannelBuffer> channels = [];
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim dataAvailable = new(0, 1);
    private readonly TaskCompletionSource pumpCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread pumpThread;
    private readonly Task pump;
    private readonly int frameSamples;
    private readonly int outputFrameSamples;
    private readonly int startupBufferedFrames;
    private readonly int maximumBufferedSamples;
    private readonly double[] leftMix;
    private readonly double[]? rightMix;
    private readonly short[] outputFrame;
    private readonly short[] silentInputFrame;
    private readonly bool supportsBufferedPlayout;
    private int nextChannelId;
    private int targetOutputBufferedFrames = NormalOutputBufferedFrames;
    private long recoveryHoldUntilTimestamp;
    private bool outputWasPrimed;
    private bool inputDiscarded;
    private bool stopping;
    private bool draining;
    private bool disposed;
    private Exception? failure;
    private long droppedSamples;
    private long overflowResynchronizations;
    private long protectedFrames;
    private long lowBufferRecoveries;
    private long latePumpWakes;
    private TimeSpan maximumPumpLateness;
    private int peakBufferedFrames;
    private long pendingSignalTimestamp;
    private string? lastDroppedLane;
    private long lastDroppedLaneSamples;
    private long gapFilledSamples;
    private long suppressedLiveConcealmentSamples;
    private long transitionDiscardedSamples;

    public AudioMixer(IAudioPlayback output)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        if (output.Format.Channels is not (1 or 2) || output.Format.BitsPerSample != 16)
            throw new NotSupportedException("Audio mixing supports mono or stereo 16-bit PCM output only.");

        frameSamples = Math.Max(1, output.Format.SampleRate / 50);
        outputFrameSamples = checked(frameSamples * output.Format.Channels);
        supportsBufferedPlayout = output.QueuedSamples is not null;
        startupBufferedFrames = !supportsBufferedPlayout
            ? 1
            : DefaultStartupBufferedFrames;
        inputFormat = new PcmAudioFormat(output.Format.SampleRate, 1, output.Format.BitsPerSample);
        maximumBufferedSamples = checked(frameSamples * MaximumBufferedFrames);
        leftMix = new double[frameSamples];
        rightMix = output.Format.Channels == 2 ? new double[frameSamples] : null;
        outputFrame = new short[outputFrameSamples];
        silentInputFrame = new short[frameSamples];
        pump = pumpCompletion.Task;
        pumpThread = new Thread(() => PumpLoop(cancellation.Token))
        {
            IsBackground = true,
            Name = "DVM Console RX mixer"
        };
        try
        {
            pumpThread.Priority = ThreadPriority.AboveNormal;
        }
        catch (PlatformNotSupportedException)
        {
            // The dedicated thread still avoids thread-pool continuation stalls.
        }
        pumpThread.Start();
    }

    public PcmAudioFormat Format => inputFormat;

    public int MaximumBufferedSamples => maximumBufferedSamples;

    public long DroppedSamples
    {
        get
        {
            lock (sync)
                return droppedSamples;
        }
    }

    public long ProtectedFrames
    {
        get
        {
            lock (sync)
                return protectedFrames;
        }
    }

    public AudioMixerDiagnostics GetDiagnostics()
    {
        TimeSpan? physicalOutputStarvation =
            (output as IAudioPlaybackContinuityDiagnostics)?.StarvedDuration;
        lock (sync)
        {
            return new AudioMixerDiagnostics(
                droppedSamples,
                overflowResynchronizations,
                protectedFrames,
                lowBufferRecoveries,
                latePumpWakes,
                maximumPumpLateness,
                peakBufferedFrames,
                startupBufferedFrames,
                MaximumBufferedFrames,
                targetOutputBufferedFrames,
                lastDroppedLane,
                lastDroppedLaneSamples,
                gapFilledSamples,
                suppressedLiveConcealmentSamples,
                transitionDiscardedSamples,
                physicalOutputStarvation);
        }
    }

    // Used only while a cold Bluetooth microphone changes the shared hardware
    // profile. Decoder/TAR observation continues, but live PCM is intentionally
    // not accumulated for delayed playback after the transition.
    public long SetInputDiscarded(bool discarded)
    {
        bool endExpectedPlayback = false;
        long discardedSamples;
        lock (sync)
        {
            ThrowIfUnavailable();
            if (inputDiscarded == discarded)
                return transitionDiscardedSamples;

            inputDiscarded = discarded;
            if (!discarded)
                return transitionDiscardedSamples;

            endExpectedPlayback = outputWasPrimed;
            outputWasPrimed = false;
            targetOutputBufferedFrames = NormalOutputBufferedFrames;
            recoveryHoldUntilTimestamp = 0;

            List<ChannelBuffer>? drainedChannels = null;
            foreach (ChannelBuffer channel in channels.Values)
            {
                while (channel.Frames.TryDequeue(out short[]? frame))
                    transitionDiscardedSamples += frame.Length;
                transitionDiscardedSamples += channel.PartialCount;
                channel.PartialCount = 0;
                channel.PlayoutStarted = false;
                channel.BoundarySmoothingPending = false;
                channel.PresentedGapSamples = 0;
                if (channel.Completing)
                {
                    drainedChannels ??= [];
                    drainedChannels.Add(channel);
                }
            }
            if (drainedChannels is not null)
            {
                foreach (ChannelBuffer channel in drainedChannels)
                    RemoveDrainedChannelLocked(channel);
            }
            discardedSamples = transitionDiscardedSamples;
        }

        // An intentional cold-Bluetooth transition is not a playback fault.
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
            var channel = new ChannelBuffer(id, frameSamples, label);
            channels.Add(channel.Id, channel);
            return new ChannelPlayback(this, channel);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] channelDrains;
        lock (sync)
        {
            if (disposed || stopping)
                return;

            stopping = true;
            draining = true;
            ChannelBuffer[] activeChannels = channels.Values.ToArray();
            foreach (ChannelBuffer channel in activeChannels)
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
                foreach (ChannelBuffer channel in channels.Values)
                {
                    channel.Disposed = true;
                    channel.DrainCompletion.TrySetResult();
                }
                channels.Clear();
                cancellation.Cancel();
                SignalDataAvailable();
            }

            try
            {
                await pump.ConfigureAwait(false);
            }
            finally
            {
                dataAvailable.Dispose();
                cancellation.Dispose();
                await output.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void PumpLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                long waitStarted = Stopwatch.GetTimestamp();
                bool signaled = dataAvailable.Wait(PumpInterval, cancellationToken);
                long now = Stopwatch.GetTimestamp();
                TimeSpan lateness = TimeSpan.Zero;
                if (signaled)
                {
                    long signalTimestamp = Interlocked.Exchange(ref pendingSignalTimestamp, 0);
                    if (signalTimestamp > 0)
                        lateness = Stopwatch.GetElapsedTime(signalTimestamp, now);
                }
                else
                {
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(waitStarted, now);
                    lateness = elapsed - PumpInterval;
                }

                lock (sync)
                {
                    if (lateness >= PumpInterval && CanProduceFrameLocked())
                    {
                        latePumpWakes++;
                        if (lateness > maximumPumpLateness)
                            maximumPumpLateness = lateness;
                    }
                }

                if (signaled)
                {
                    bool shouldCoalesceFirstFrame;
                    lock (sync)
                        shouldCoalesceFirstFrame = !outputWasPrimed;
                    if (shouldCoalesceFirstFrame && cancellationToken.WaitHandle.WaitOne(PumpInterval))
                        cancellationToken.ThrowIfCancellationRequested();
                }

                int framesToWrite = FramesNeededForOutputBuffer();
                for (int index = 0; index < framesToWrite; index++)
                {
                    TimeSpan presentationDelay = GetPhysicalQueueDuration();
                    if (!TryTakeFrame(
                            out ReadOnlyMemory<short> frame,
                            out PresentationNotification[] notifications))
                        break;
                    output.WriteAsync(frame, cancellationToken).GetAwaiter().GetResult();
                    lock (sync)
                        outputWasPrimed = true;
                    NotifyPresentations(notifications, presentationDelay);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during mixer shutdown.
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                failure = exception;
                foreach (ChannelBuffer channel in channels.Values)
                    channel.DrainCompletion.TrySetException(exception);
            }
        }
        finally
        {
            pumpCompletion.TrySetResult();
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
            return 1;

        long now = Stopwatch.GetTimestamp();
        int targetFrames;
        bool endExpectedPlayback = false;
        lock (sync)
        {
            bool hasReadyFrames = HasReadyFramesLocked();
            bool expectsMoreInput = ExpectsMoreLiveInputLocked();
            if (!hasReadyFrames && queuedSamples == 0 && !expectsMoreInput)
            {
                endExpectedPlayback = outputWasPrimed;
                outputWasPrimed = false;
                targetOutputBufferedFrames = NormalOutputBufferedFrames;
                recoveryHoldUntilTimestamp = 0;
            }
            if (hasReadyFrames && outputWasPrimed && queuedSamples < outputFrameSamples)
            {
                lowBufferRecoveries++;
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

    private static void NotifyPresentations(
        IReadOnlyList<PresentationNotification> notifications,
        TimeSpan presentationDelay)
    {
        foreach (PresentationNotification notification in notifications)
        {
            try
            {
                notification.Observer(notification.Samples, presentationDelay);
            }
            catch
            {
                // Presentation observers are diagnostic/UI consumers and must
                // never stop the real-time mixer thread.
            }
        }
    }

    private bool TryTakeFrame(
        out ReadOnlyMemory<short> frame,
        out PresentationNotification[] notifications)
    {
        lock (sync)
        {
            if (!CanProduceFrameLocked())
            {
                frame = default;
                notifications = [];
                return false;
            }

            Array.Clear(leftMix);
            if (rightMix is not null)
                Array.Clear(rightMix);
            List<ChannelBuffer>? drainedChannels = null;
            List<PresentationNotification>? presented = null;
            foreach (ChannelBuffer channel in channels.Values)
            {
                if (!channel.PlayoutStarted)
                    continue;

                if (!channel.Frames.TryDequeue(out short[]? source))
                {
                    if (!supportsBufferedPlayout || channel.Completing || !channel.LivePlaybackEnabled)
                        continue;

                    channel.PresentedGapSamples = checked(
                        channel.PresentedGapSamples + frameSamples);
                    gapFilledSamples = checked(gapFilledSamples + frameSamples);
                    channel.LastOutputSample = 0;
                    channel.HasLastOutputSample = true;
                    channel.BoundarySmoothingPending = true;
                    if (channel.PresentationObserver is not null)
                    {
                        (presented ??= []).Add(new PresentationNotification(
                            channel.PresentationObserver,
                            silentInputFrame));
                    }
                    continue;
                }

                SmoothCorrectedBoundary(channel, source);

                if (channel.PresentationObserver is not null)
                {
                    (presented ??= []).Add(new PresentationNotification(
                        channel.PresentationObserver,
                        source));
                }

                int count = Math.Min(frameSamples, source.Length);
                double leftBalance = rightMix is null || channel.Balance <= 0 ? 1.0 : 1.0 - channel.Balance;
                double rightBalance = channel.Balance >= 0 ? 1.0 : 1.0 + channel.Balance;
                for (int index = 0; index < count; index++)
                {
                    double gained = source[index] * channel.Gain;
                    leftMix[index] += gained * leftBalance;
                    if (rightMix is not null)
                        rightMix[index] += gained * rightBalance;
                }

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
                foreach (ChannelBuffer channel in drainedChannels)
                    RemoveDrainedChannelLocked(channel);
            }

            double peak = 0;
            for (int index = 0; index < frameSamples; index++)
            {
                peak = Math.Max(peak, Math.Abs(leftMix[index]));
                if (rightMix is not null)
                    peak = Math.Max(peak, Math.Abs(rightMix[index]));
            }
            double protection = peak > short.MaxValue ? short.MaxValue / peak : 1.0;
            if (protection < 1.0)
                protectedFrames++;

            for (int index = 0; index < frameSamples; index++)
            {
                int outputIndex = index * output.Format.Channels;
                outputFrame[outputIndex] = ToPcm(leftMix[index] * protection);
                if (rightMix is not null)
                    outputFrame[outputIndex + 1] = ToPcm(rightMix[index] * protection);
            }
            frame = outputFrame;
            notifications = presented?.ToArray() ?? [];
            return true;
        }
    }

    private bool HasReadyFramesLocked()
    {
        foreach (ChannelBuffer channel in channels.Values)
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
            channel.PlayoutStarted &&
            !channel.Completing);

    private void Enqueue(
        ChannelBuffer channel,
        ReadOnlyMemory<short> samples,
        bool concealment,
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
                transitionDiscardedSamples += samples.Length;
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
                    suppressedLiveConcealmentSamples += alreadyPresented;
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
                    suppressedLiveConcealmentSamples += suppressedSamples;
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

    private void QueueFrameLocked(ChannelBuffer channel, short[] frame)
    {
        bool overflowCorrected = false;
        if (channel.Frames.Count >= MaximumBufferedFrames)
        {
            while (channel.Frames.Count > OverflowRecoveryFrames &&
                   channel.Frames.TryDequeue(out short[]? discarded))
            {
                overflowCorrected = true;
                channel.DroppedSamples += discarded.Length;
                droppedSamples += discarded.Length;
                lastDroppedLane = channel.DiagnosticLabel;
                lastDroppedLaneSamples = channel.DroppedSamples;
            }
        }
        if (overflowCorrected)
        {
            overflowResynchronizations++;
            channel.BoundarySmoothingPending = channel.HasLastOutputSample;
        }

        channel.Frames.Enqueue(frame);
        if (!channel.PlayoutStarted &&
            (channel.Completing || channel.Frames.Count >= startupBufferedFrames))
        {
            channel.PlayoutStarted = true;
        }
        peakBufferedFrames = Math.Max(peakBufferedFrames, channel.Frames.Count);
        SignalDataAvailable();
    }

    private static void SmoothCorrectedBoundary(ChannelBuffer channel, short[] source)
    {
        if (!channel.BoundarySmoothingPending || !channel.HasLastOutputSample || source.Length == 0)
            return;

        int count = Math.Min(BoundarySmoothingSamples, source.Length);
        double start = channel.LastOutputSample;
        for (int index = 0; index < count; index++)
        {
            double progress = (index + 1.0) / count;
            source[index] = ToPcm(start + ((source[index] - start) * progress));
        }
        channel.BoundarySmoothingPending = false;
    }

    private void Complete(ChannelBuffer channel)
    {
        lock (sync)
        {
            if (channel.Disposed)
                return;
            CompleteChannelLocked(channel);
        }
    }

    private bool GetLivePlaybackEnabled(ChannelBuffer channel)
    {
        lock (sync)
            return channel.LivePlaybackEnabled;
    }

    private void SetLivePlaybackEnabled(ChannelBuffer channel, bool enabled)
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
            channel.PlayoutStarted = false;
            channel.BoundarySmoothingPending = false;
            channel.HasLastOutputSample = false;
            channel.PresentedGapSamples = 0;
        }
    }

    private void Remove(ChannelBuffer channel)
    {
        lock (sync)
        {
            if (channel.Disposed)
                return;

            channel.Frames.Clear();
            channel.PartialCount = 0;
            RemoveDrainedChannelLocked(channel);
        }
    }

    private void CompleteChannelLocked(ChannelBuffer channel)
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

    private void RemoveDrainedChannelLocked(ChannelBuffer channel)
    {
        channel.Disposed = true;
        channels.Remove(channel.Id);
        channel.DrainCompletion.TrySetResult();
    }

    private void SignalDataAvailable()
    {
        Interlocked.CompareExchange(
            ref pendingSignalTimestamp,
            Stopwatch.GetTimestamp(),
            comparand: 0);
        try
        {
            dataAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake is sufficient; the pump drains to its target.
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(disposed || stopping, this);
        if (failure is not null)
            throw new IOException("The shared audio mixer stopped.", failure);
    }

    private static short ToPcm(double sample)
        => (short)Math.Clamp(
            Math.Round(sample, MidpointRounding.AwayFromZero),
            short.MinValue,
            short.MaxValue);

    private readonly record struct PresentationNotification(
        Action<ReadOnlyMemory<short>, TimeSpan> Observer,
        ReadOnlyMemory<short> Samples);

    private sealed class ChannelBuffer(int id, int frameSamples, string diagnosticLabel)
    {
        public int Id { get; } = id;
        public string DiagnosticLabel { get; } = diagnosticLabel;
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
        public TaskCompletionSource DrainCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; set; }
    }

    private sealed class ChannelPlayback(AudioMixer owner, ChannelBuffer channel) :
        IAudioPlayback,
        IConcealmentAudioPlayback,
        ILiveAudioPlaybackControl,
        IAudioPlaybackPresentationSource,
        IAudioGainControl,
        IAudioBalanceControl
    {
        private bool disposed;

        public PcmAudioFormat Format => owner.Format;

        public bool LivePlaybackEnabled
        {
            get => owner.GetLivePlaybackEnabled(channel);
            set
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                owner.SetLivePlaybackEnabled(channel, value);
            }
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
            owner.Enqueue(channel, samples, concealment: false, cancellationToken);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteConcealmentAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Enqueue(channel, samples, concealment: true, cancellationToken);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            owner.Complete(channel);
            return ValueTask.CompletedTask;
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
