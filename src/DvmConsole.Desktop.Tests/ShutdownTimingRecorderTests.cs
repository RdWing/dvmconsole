// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ShutdownTimingRecorderTests
{
    [Fact]
    public async Task PersistsOnlyNamesAndDurationsForTheLastShutdown()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-shutdown-timing-tests",
            Guid.NewGuid().ToString("N"));
        var recorder = new ShutdownTimingRecorder(root);
        try
        {
            recorder.Begin();
            await recorder.MeasureAsync("window-cleanup", () => Task.Delay(10));
            recorder.ObserveService(new ConsoleSessionServiceDisposalTiming(
                "receive",
                "audio-work",
                TimeSpan.FromMilliseconds(12.5)));
            recorder.Complete();

            string[] lines = File.ReadAllLines(recorder.Path);
            Assert.Contains(lines, line => line.EndsWith("\twindow-cleanup", StringComparison.Ordinal));
            Assert.Contains(lines, line => line == "12.5\tservice:receive/audio-work");
            Assert.EndsWith("\ttotal", lines[^1], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
