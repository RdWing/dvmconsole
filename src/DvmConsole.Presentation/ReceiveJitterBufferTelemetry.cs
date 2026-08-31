namespace DvmConsole.Presentation;

public readonly record struct ReceiveJitterBufferTelemetry(
    TimeSpan P25LearnedDelay,
    TimeSpan DmrLearnedDelay,
    TimeSpan NxdnLearnedDelay,
    bool P25Adaptive,
    bool DmrAdaptive,
    bool NxdnAdaptive,
    long RestoredDelayedPackets,
    long DeadlineMissedPackets)
{
    public string LearnedText
    {
        get
        {
            var learned = new List<string>(3);
            if (P25Adaptive)
                learned.Add($"P25 {P25LearnedDelay.TotalMilliseconds:0} ms");
            if (DmrAdaptive)
                learned.Add($"DMR {DmrLearnedDelay.TotalMilliseconds:0} ms");
            if (NxdnAdaptive)
                learned.Add($"NXDN {NxdnLearnedDelay.TotalMilliseconds:0} ms");

            return learned.Count == 0
                ? "Adaptive learned · disabled"
                : $"Adaptive learned · {string.Join(" · ", learned)}";
        }
    }

    public string EffectivenessText
        => $"Jitter effectiveness · restored {RestoredDelayedPackets:N0} delayed " +
           $"{Pluralize(RestoredDelayedPackets, "packet", "packets")} before playout · " +
           $"deadline misses {DeadlineMissedPackets:N0}";

    private static string Pluralize(long count, string singular, string plural)
        => count == 1 ? singular : plural;
}
