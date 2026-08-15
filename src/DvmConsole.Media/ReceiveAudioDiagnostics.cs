namespace DvmConsole.Media;

public sealed record ReceiveAudioDiagnostics(
    int FramesDecoded,
    long LostPackets,
    long DuplicateOrLatePackets,
    long MalformedPackets)
{
    public bool HasIssues
        => LostPackets > 0 || DuplicateOrLatePackets > 0 || MalformedPackets > 0;

    public string SummaryText
    {
        get
        {
            var details = new List<string>();
            if (LostPackets > 0)
                details.Add($"lost {LostPackets:N0}");
            if (DuplicateOrLatePackets > 0)
                details.Add($"late/duplicate {DuplicateOrLatePackets:N0}");
            if (MalformedPackets > 0)
                details.Add($"malformed {MalformedPackets:N0}");
            return details.Count == 0
                ? $"{FramesDecoded:N0} decoded frames"
                : string.Join(", ", details);
        }
    }
}
