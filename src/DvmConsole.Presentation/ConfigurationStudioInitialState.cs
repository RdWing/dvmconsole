namespace DvmConsole.Presentation;

public sealed record ConfigurationStudioPosition(double X, double Y);

public sealed record ConfigurationStudioInitialState(
    IReadOnlyDictionary<string, ConfigurationStudioPosition> ChannelPositions,
    IReadOnlyDictionary<string, string> ZoneSystemAssignments,
    IReadOnlyCollection<string> CallPrioritySystemNames);
