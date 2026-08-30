using DvmConsole.Core.Diagnostics;
using fnecore;

namespace DvmConsole.FneClient;

internal static class FneLogInterpreter
{
    public static bool IsRoutineHealthyKeepalive(string message)
        => message.Contains("RPTPING sent", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("MSTPONG received", StringComparison.OrdinalIgnoreCase);

    public static DebugLogSeverity MapSeverity(LogLevel level)
        => level switch
        {
            LogLevel.DEBUG => DebugLogSeverity.Debug,
            LogLevel.WARNING => DebugLogSeverity.Warning,
            LogLevel.ERROR => DebugLogSeverity.Error,
            LogLevel.FATAL => DebugLogSeverity.Fatal,
            _ => DebugLogSeverity.Info
        };

}
