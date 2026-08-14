// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using fnecore;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Process-level fnecore diagnostic controls. The fnecore level is an
    /// inclusive numeric threshold, so FATAL is the value that captures all
    /// levels; packet hex dumps remain opt-in while decoded traffic summaries
    /// are enabled unless explicitly disabled.
    /// </summary>
    public sealed class FneLoggingOptions
    {
        public LogLevel LogLevel { get; }
        public bool RawPacketTrace { get; }
        public bool TrafficLogging { get; }

        private FneLoggingOptions(LogLevel logLevel, bool rawPacketTrace, bool trafficLogging)
        {
            LogLevel = logLevel;
            RawPacketTrace = rawPacketTrace;
            TrafficLogging = trafficLogging;
        }

        public static FneLoggingOptions FromEnvironment()
            => FromValues(
                Environment.GetEnvironmentVariable("DVMCONSOLE_FNE_LOG_LEVEL"),
                Environment.GetEnvironmentVariable("DVMCONSOLE_FNE_RAW_PACKET_TRACE"),
                Environment.GetEnvironmentVariable("DVMCONSOLE_FNE_TRAFFIC_LOGGING"));

        public static FneLoggingOptions FromValues(
            string? logLevel,
            string? rawPacketTrace,
            string? trafficLogging)
        {
            return new FneLoggingOptions(
                ParseLogLevel(logLevel),
                ParseFlag(rawPacketTrace),
                trafficLogging is null || ParseFlag(trafficLogging));
        }

        private static LogLevel ParseLogLevel(string? value)
        {
            if (Enum.TryParse(value, ignoreCase: true, out LogLevel parsed)
                && parsed is LogLevel.INFO
                    or LogLevel.WARNING
                    or LogLevel.ERROR
                    or LogLevel.DEBUG
                    or LogLevel.FATAL)
            {
                return parsed;
            }

            return LogLevel.FATAL;
        }

        private static bool ParseFlag(string? value)
            => value is not null
                && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}