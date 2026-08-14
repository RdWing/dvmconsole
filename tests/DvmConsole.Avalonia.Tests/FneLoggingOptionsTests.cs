// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using DvmConsole.Avalonia.Services;
using fnecore;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class FneLoggingOptionsTests
    {
        [Fact]
        public void FromValues_DefaultsToFatalWithTrafficSummaries()
        {
            var options = FneLoggingOptions.FromValues(null, null, null);

            Assert.Equal(LogLevel.FATAL, options.LogLevel);
            Assert.False(options.RawPacketTrace);
            Assert.True(options.TrafficLogging);
        }

        [Fact]
        public void FromValues_EnablesDebugTrafficDiagnostics()
        {
            var options = FneLoggingOptions.FromValues("DEBUG", "1", "true");

            Assert.Equal(LogLevel.DEBUG, options.LogLevel);
            Assert.True(options.RawPacketTrace);
            Assert.True(options.TrafficLogging);
        }

        [Fact]
        public void FromValues_InvalidValuesUseSafeDefaults()
        {
            var options = FneLoggingOptions.FromValues("not-a-level", "no", "off");

            Assert.Equal(LogLevel.FATAL, options.LogLevel);
            Assert.False(options.RawPacketTrace);
            Assert.False(options.TrafficLogging);
        }
    }
}