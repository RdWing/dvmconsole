using DvmConsole.Core.Diagnostics;
using fnecore;

namespace DvmConsole.FneClient;

internal sealed record FneLogStatusUpdate(FneConnectionState State, string Message);

internal static class FneLogInterpreter
{
    public static bool IsLoginRequest(string message)
        => message.Contains("Sending login request", StringComparison.OrdinalIgnoreCase);

    public static bool IsLoginAcknowledgement(string message)
        => message.Contains("login ACK received", StringComparison.OrdinalIgnoreCase);

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

    public static FneLogStatusUpdate? InterpretStatus(
        string message,
        FneConnectionState currentState)
    {
        if (IsLoginRequest(message))
        {
            return new FneLogStatusUpdate(
                FneConnectionState.WaitingForLogin,
                "FNE login request sent");
        }

        if (message.Contains("Network Sent", StringComparison.OrdinalIgnoreCase))
            return new FneLogStatusUpdate(currentState, "FNE traffic packet sent");

        if (message.Contains("Network Received", StringComparison.OrdinalIgnoreCase))
            return new FneLogStatusUpdate(currentState, "FNE traffic packet received");

        if (IsLoginAcknowledgement(message))
        {
            return new FneLogStatusUpdate(
                FneConnectionState.Authenticating,
                "FNE login acknowledgement received");
        }

        if (message.Contains("master NAK", StringComparison.OrdinalIgnoreCase))
        {
            return new FneLogStatusUpdate(
                FneConnectionState.Faulted,
                "FNE master rejected the connection");
        }

        if (message.Contains("SOCKET ERROR", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Not connected or lost connection", StringComparison.OrdinalIgnoreCase))
        {
            return new FneLogStatusUpdate(
                FneConnectionState.Faulted,
                "FNE socket error or connection loss");
        }

        return null;
    }
}
