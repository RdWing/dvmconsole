// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleReceiveDiagnosticsTests
{
    [Fact]
    public async Task ReadsSharedCountersAndCoalescesWarningsWithoutOpeningAudio()
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Dispatch", "Test", "p25", 100, 0));
        var directory = new ConsoleChannelMediaDirectory([(channel, new RadioAliasIndex([]))]);
        await using var audio = new ChannelReceiveAudioCoordinator(
            () => throw new InvalidOperationException("Diagnostics must not open audio."),
            () => throw new InvalidOperationException("Diagnostics must not open a vocoder."));
        await using var work = new ChannelReceiveWorkQueue((_, _) => Task.CompletedTask);
        var published = new List<ConsoleReceiveDiagnostic>();
        var diagnostics = new ConsoleReceiveDiagnostics(directory, audio, work, published.Add);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        diagnostics.Inspect(channel.Id, 10, now);
        Assert.Empty(published);
        channel.Receive.RecordDroppedFrame();
        diagnostics.Inspect(channel.Id, 10, now.AddMilliseconds(250));
        var first = Assert.Single(published);
        Assert.True(first.ShowStatus);
        Assert.Equal(DebugLogSeverity.Warning, first.Severity);
        Assert.Contains("Dispatch", first.Message);
        Assert.Contains("receive queue dropped 1", first.Message);
        channel.Receive.RecordDroppedFrame();
        diagnostics.Inspect(channel.Id, 10, now.AddSeconds(1));
        Assert.Single(published);
        diagnostics.Inspect(channel.Id, 10, now.AddSeconds(6));
        Assert.Equal(2, published.Count);
        Assert.Contains("receive queue dropped 2", published[1].Message);
    }
}
