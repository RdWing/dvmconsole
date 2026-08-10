// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract for the Core-owned headless recent-log buffer.
    /// Rendering, file/trace/console sinks, and static global logging remain
    /// frontend seams.
    /// </summary>
    public sealed class LogBufferContractTests
    {
        [Fact]
        public void NewBuffer_IsEmptyAndUsesTheWpfRecentLineCapacity()
        {
            var buffer = new LogBuffer();

            Assert.Empty(buffer.GetRecentLines());
            Assert.Equal(500, buffer.Capacity);
        }

        [Fact]
        public void WriteLine_RetainsNewest500LinesAndEvictsOldest()
        {
            var buffer = new LogBuffer();

            for (int index = 0; index < 501; index++)
                buffer.WriteLine($"line-{index}");

            IReadOnlyList<string> lines = buffer.GetRecentLines();
            Assert.Equal(500, lines.Count);
            Assert.Equal("line-1", lines[0]);
            Assert.Equal("line-500", lines[^1]);
        }

        [Fact]
        public void WriteLine_RaisesAfterTheLineIsVisibleInTheSnapshot()
        {
            var buffer = new LogBuffer();
            string? observed = null;
            IReadOnlyList<string>? observedSnapshot = null;
            buffer.LogLineWritten += line =>
            {
                observed = line;
                observedSnapshot = buffer.GetRecentLines();
            };

            buffer.WriteLine("rendered line");

            Assert.Equal("rendered line", observed);
            Assert.NotNull(observedSnapshot);
            Assert.Contains("rendered line", observedSnapshot!);
        }

        [Fact]
        public void GetRecentLines_ReturnsAnIndependentSnapshot()
        {
            var buffer = new LogBuffer();
            buffer.WriteLine("one");
            IReadOnlyList<string> first = buffer.GetRecentLines();

            buffer.WriteLine("two");
            IReadOnlyList<string> second = buffer.GetRecentLines();

            Assert.Single(first);
            Assert.Equal("one", first[0]);
            Assert.Equal(new[] { "one", "two" }, second);
        }
    }
}
