using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

// Learns transport delay variation once per FNE connection and protocol.
// Packet queues remain stream-owned; this controller only selects the target
// that a new stream snapshots for its lifetime.
internal sealed class AdaptiveReceiveJitterBufferController
{
    private readonly object sync = new();
    private readonly IMonotonicTimeSource timeSource;
    private readonly Dictionary<string, ConnectionEstimator> connections =
        new(StringComparer.OrdinalIgnoreCase);

    public AdaptiveReceiveJitterBufferController(IMonotonicTimeSource? timeSource = null)
    {
        this.timeSource = timeSource ?? SystemReceiveWorkQueueScheduler.Instance;
    }

    public ReceiveJitterBufferProfile GetProfile(
        string connectionName,
        RadioMediaProtocol protocol,
        ReceiveJitterBufferConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        if (!configuration.IsAdaptive)
            return configuration.CreateProfile(configuration.InitialDelay);

        lock (sync)
        {
            ProtocolEstimator estimator = GetOrCreateEstimator(
                connectionName,
                protocol,
                configuration);
            return configuration.CreateProfile(estimator.TargetDelay);
        }
    }

    public void Observe(
        string connectionName,
        IRadioMediaFrame traffic,
        long transportIngressTimestamp,
        ReceiveJitterBufferConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(traffic);
        if (!configuration.IsAdaptive || traffic.StreamId == 0)
            return;

        lock (sync)
        {
            ProtocolEstimator estimator = GetOrCreateEstimator(
                connectionName,
                traffic.Protocol,
                configuration);
            if (RadioReceiveTrafficClassifier.IsTerminator(traffic))
            {
                estimator.CompleteStream(traffic.StreamId);
                return;
            }

            if (!RadioReceiveTrafficClassifier.CarriesEncodedVoicePayload(traffic) ||
                transportIngressTimestamp <= 0)
            {
                return;
            }

            estimator.Observe(
                traffic.StreamId,
                traffic.PacketSequence,
                transportIngressTimestamp);
        }
    }

    public void Reset(string connectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        lock (sync)
            connections.Remove(connectionName);
    }

    private ProtocolEstimator GetOrCreateEstimator(
        string connectionName,
        RadioMediaProtocol protocol,
        ReceiveJitterBufferConfiguration configuration)
    {
        if (!connections.TryGetValue(connectionName, out ConnectionEstimator? connection))
        {
            connection = new ConnectionEstimator(timeSource);
            connections.Add(connectionName, connection);
        }
        return connection.GetOrCreate(protocol, configuration);
    }

    private sealed class ConnectionEstimator
    {
        private readonly IMonotonicTimeSource timeSource;
        private readonly Dictionary<RadioMediaProtocol, ProtocolEstimator> protocols = [];

        public ConnectionEstimator(IMonotonicTimeSource timeSource)
        {
            this.timeSource = timeSource;
        }

        public ProtocolEstimator GetOrCreate(
            RadioMediaProtocol protocol,
            ReceiveJitterBufferConfiguration configuration)
        {
            if (!protocols.TryGetValue(protocol, out ProtocolEstimator? estimator))
            {
                estimator = new ProtocolEstimator(configuration, timeSource);
                protocols.Add(protocol, estimator);
            }
            else
            {
                estimator.ApplyConfiguration(configuration);
            }
            return estimator;
        }
    }

    private sealed class ProtocolEstimator
    {
        private const int MaximumTrackedStreams = 64;
        private const int CleanCallsBeforeDecrease = 3;
        private static readonly TimeSpan StreamRestartGap = TimeSpan.FromSeconds(5);
        private readonly Dictionary<uint, StreamDelayObservation> streams = [];
        private readonly IMonotonicTimeSource timeSource;
        private ReceiveJitterBufferConfiguration configuration;
        private int consecutiveCleanCalls;

        public ProtocolEstimator(
            ReceiveJitterBufferConfiguration configuration,
            IMonotonicTimeSource timeSource)
        {
            this.configuration = configuration;
            this.timeSource = timeSource;
            TargetDelay = configuration.InitialDelay;
        }

        public TimeSpan TargetDelay { get; private set; }

        public void ApplyConfiguration(ReceiveJitterBufferConfiguration next)
        {
            if (next.PacketDuration != configuration.PacketDuration)
            {
                streams.Clear();
                consecutiveCleanCalls = 0;
                TargetDelay = next.InitialDelay;
            }
            configuration = next;
            TargetDelay = ClampToConfiguration(TargetDelay);
        }

        public void Observe(uint streamId, ushort sequence, long arrivalTimestamp)
        {
            if (!streams.TryGetValue(streamId, out StreamDelayObservation? stream) ||
                stream.HasRestarted(arrivalTimestamp, StreamRestartGap))
            {
                MakeRoomForStream();
                stream = new StreamDelayObservation(
                    sequence,
                    arrivalTimestamp,
                    configuration.PacketDuration,
                    timeSource);
                streams[streamId] = stream;
                return;
            }

            TimeSpan? requiredDelay = stream.Observe(sequence, arrivalTimestamp);
            if (requiredDelay is not TimeSpan required)
                return;

            TimeSpan packetAligned = RoundUpToPacket(required);
            if (packetAligned > TargetDelay)
            {
                TargetDelay = Min(packetAligned, configuration.MaximumDelay);
                consecutiveCleanCalls = 0;
            }
        }

        public void CompleteStream(uint streamId)
        {
            if (!streams.Remove(streamId, out StreamDelayObservation? stream))
                return;

            bool hasLowerStableTier = TargetDelay > TimeSpan.Zero;
            bool clean = stream.SampleCount > 0 &&
                stream.PeakRequiredDelay <= Max(
                    TimeSpan.Zero,
                    TargetDelay - configuration.PacketDuration);
            if (!hasLowerStableTier || !clean)
            {
                consecutiveCleanCalls = 0;
                return;
            }

            consecutiveCleanCalls++;
            if (consecutiveCleanCalls < CleanCallsBeforeDecrease)
                return;

            TargetDelay = Max(
                TimeSpan.Zero,
                TargetDelay - configuration.PacketDuration);
            consecutiveCleanCalls = 0;
        }

        private void MakeRoomForStream()
        {
            if (streams.Count < MaximumTrackedStreams)
                return;

            KeyValuePair<uint, StreamDelayObservation> oldest = streams
                .MinBy(entry => entry.Value.LastArrivalTimestamp);
            streams.Remove(oldest.Key);
        }

        private TimeSpan RoundUpToPacket(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero)
                return TimeSpan.Zero;
            if (delay <= configuration.PacketDuration)
                return configuration.PacketDuration;

            long packetTicks = configuration.PacketDuration.Ticks;
            long packetCount = checked((delay.Ticks + packetTicks - 1) / packetTicks);
            return TimeSpan.FromTicks(checked(packetCount * packetTicks));
        }

        private TimeSpan ClampToConfiguration(TimeSpan delay)
            => Min(
                Max(delay, TimeSpan.Zero),
                configuration.MaximumDelay);

        private static TimeSpan Min(TimeSpan left, TimeSpan right)
            => left <= right ? left : right;

        private static TimeSpan Max(TimeSpan left, TimeSpan right)
            => left >= right ? left : right;
    }

    private sealed class StreamDelayObservation
    {
        private const int SequenceModulus = ushort.MaxValue;
        private const int MaximumSequenceDistance = SequenceModulus / 2;
        private readonly ushort originSequence;
        private readonly long originArrivalTimestamp;
        private readonly long packetDurationTimestampTicks;
        private readonly IMonotonicTimeSource timeSource;
        private long highestUnwrappedDistance;
        private long minimumTransitTimestampTicks;

        public StreamDelayObservation(
            ushort originSequence,
            long originArrivalTimestamp,
            TimeSpan packetDuration,
            IMonotonicTimeSource timeSource)
        {
            this.originSequence = originSequence;
            this.originArrivalTimestamp = originArrivalTimestamp;
            this.timeSource = timeSource;
            packetDurationTimestampTicks = ToStopwatchTicks(packetDuration);
            LastArrivalTimestamp = originArrivalTimestamp;
        }

        public int SampleCount { get; private set; }
        public TimeSpan PeakRequiredDelay { get; private set; }
        public long LastArrivalTimestamp { get; private set; }

        public bool HasRestarted(long arrivalTimestamp, TimeSpan restartGap)
            => arrivalTimestamp > LastArrivalTimestamp &&
               timeSource.GetElapsedTime(LastArrivalTimestamp, arrivalTimestamp) >= restartGap;

        public TimeSpan? Observe(ushort sequence, long arrivalTimestamp)
        {
            if (arrivalTimestamp <= LastArrivalTimestamp)
                return null;
            LastArrivalTimestamp = arrivalTimestamp;
            long unwrappedDistance = UnwrapDistance(sequence);
            if (unwrappedDistance <= 0)
                return null;

            highestUnwrappedDistance = Math.Max(highestUnwrappedDistance, unwrappedDistance);
            long expectedTimestamp = checked(
                originArrivalTimestamp + unwrappedDistance * packetDurationTimestampTicks);
            long transit = arrivalTimestamp - expectedTimestamp;
            minimumTransitTimestampTicks = Math.Min(minimumTransitTimestampTicks, transit);
            long relativeDelayTicks = Math.Max(0, transit - minimumTransitTimestampTicks);
            TimeSpan requiredDelay = FromStopwatchTicks(relativeDelayTicks);
            SampleCount++;
            if (requiredDelay > PeakRequiredDelay)
                PeakRequiredDelay = requiredDelay;
            return requiredDelay;
        }

        private long UnwrapDistance(ushort sequence)
        {
            long distance = (sequence - originSequence + SequenceModulus) % SequenceModulus;
            while (distance + MaximumSequenceDistance < highestUnwrappedDistance)
                distance += SequenceModulus;
            while (distance - MaximumSequenceDistance > highestUnwrappedDistance)
                distance -= SequenceModulus;
            return distance;
        }

        private long ToStopwatchTicks(TimeSpan duration)
            => checked((long)Math.Round(duration.TotalSeconds * timeSource.TimestampFrequency));

        private TimeSpan FromStopwatchTicks(long ticks)
            => TimeSpan.FromSeconds(ticks / (double)timeSource.TimestampFrequency);
    }
}
