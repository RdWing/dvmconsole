namespace DvmConsole.Desktop;

internal readonly record struct ReceiveWarningDiagnostics(
    long RtpLostPackets,
    long RtpLateOrDuplicatePackets,
    long ReceiveQueueDroppedFrames,
    long PostCallLateFrames,
    long MalformedPackets)
{
    public bool HasIssues =>
        RtpLostPackets > 0 ||
        RtpLateOrDuplicatePackets > 0 ||
        ReceiveQueueDroppedFrames > 0 ||
        PostCallLateFrames > 0 ||
        MalformedPackets > 0;

    public string SummaryText
    {
        get
        {
            var details = new List<string>(5);
            if (RtpLostPackets > 0)
                details.Add($"RTP lost {RtpLostPackets:N0}");
            if (RtpLateOrDuplicatePackets > 0)
                details.Add($"RTP late/duplicate {RtpLateOrDuplicatePackets:N0}");
            if (ReceiveQueueDroppedFrames > 0)
                details.Add($"receive queue dropped {ReceiveQueueDroppedFrames:N0}");
            if (PostCallLateFrames > 0)
                details.Add($"post-call late {PostCallLateFrames:N0}");
            if (MalformedPackets > 0)
                details.Add($"malformed {MalformedPackets:N0}");
            return details.Count == 0 ? "no packet issues" : string.Join(", ", details);
        }
    }
}

internal sealed class ReceiveDiagnosticsReporter
{
    private readonly object sync = new();
    private readonly TimeSpan minimumInterval;
    private readonly Dictionary<ChannelViewModel, ChannelState> states = [];

    public ReceiveDiagnosticsReporter(TimeSpan minimumInterval)
    {
        if (minimumInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        this.minimumInterval = minimumInterval;
    }

    public bool ShouldPublish(
        ChannelViewModel channel,
        ReceiveWarningDiagnostics diagnostics,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channel);

        ReceiveWarningDiagnostics current = diagnostics;
        lock (sync)
        {
            if (!states.TryGetValue(channel, out ChannelState? state))
            {
                state = new ChannelState();
                states.Add(channel, state);
            }

            if (!current.HasIssues)
            {
                state.LastPublished = current;
                state.Pending = null;
                state.LastPublishedAt = null;
                return false;
            }

            if (state.LastPublishedAt is null)
            {
                Publish(state, current, now);
                return true;
            }

            state.Pending = current == state.LastPublished ? null : current;
            if (state.Pending is null || now - state.LastPublishedAt.Value < minimumInterval)
                return false;

            Publish(state, state.Pending.Value, now);
            return true;
        }
    }

    private static void Publish(
        ChannelState state,
        ReceiveWarningDiagnostics snapshot,
        DateTimeOffset now)
    {
        state.LastPublished = snapshot;
        state.Pending = null;
        state.LastPublishedAt = now;
    }

    private sealed class ChannelState
    {
        public ReceiveWarningDiagnostics LastPublished { get; set; }
        public ReceiveWarningDiagnostics? Pending { get; set; }
        public DateTimeOffset? LastPublishedAt { get; set; }
    }
}
