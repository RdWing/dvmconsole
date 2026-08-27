namespace DvmConsole.Core.Diagnostics;

public static class VerboseDiagnosticLogging
{
    public const string EnvironmentVariableName = "DVMCONSOLE_VERBOSE_DIAGNOSTICS";

    public static bool IsEnabled { get; } = Parse(
        Environment.GetEnvironmentVariable(EnvironmentVariableName));

    internal static bool Parse(string? value)
        => value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
