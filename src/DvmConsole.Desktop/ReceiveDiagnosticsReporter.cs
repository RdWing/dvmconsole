using DvmConsole.Media;

namespace DvmConsole.Desktop;

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
        ReceiveAudioDiagnostics diagnostics,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(diagnostics);

        IssueSnapshot current = IssueSnapshot.From(diagnostics);
        lock (sync)
        {
            if (!states.TryGetValue(channel, out ChannelState? state))
            {
                state = new ChannelState();
                states.Add(channel, state);
            }

            if (!diagnostics.HasIssues)
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

    private static void Publish(ChannelState state, IssueSnapshot snapshot, DateTimeOffset now)
    {
        state.LastPublished = snapshot;
        state.Pending = null;
        state.LastPublishedAt = now;
    }

    private readonly record struct IssueSnapshot(
        long LostPackets,
        long DuplicateOrLatePackets,
        long MalformedPackets)
    {
        public static IssueSnapshot From(ReceiveAudioDiagnostics diagnostics)
            => new(
                diagnostics.LostPackets,
                diagnostics.DuplicateOrLatePackets,
                diagnostics.MalformedPackets);
    }

    private sealed class ChannelState
    {
        public IssueSnapshot LastPublished { get; set; }
        public IssueSnapshot? Pending { get; set; }
        public DateTimeOffset? LastPublishedAt { get; set; }
    }
}
