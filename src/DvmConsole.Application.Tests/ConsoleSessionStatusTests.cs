// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleSessionStatusTests
{
    [Fact]
    public void ReentrantObserverCannotReplayAnOlderStatusAndUnchangedFieldsKeepLatestMessage()
    {
        var status = new ConsoleSessionStatus();
        var original = status.Snapshot;
        status.Changed += (_, _) => throw new InvalidOperationException("Observer failure");
        status.Changed += (_, _) => status.SetAudio("Listening");
        var observed = new List<ConsoleSessionStatusSnapshot>();
        status.Changed += (_, _) => observed.Add(status.Snapshot);
        status.SetConsole("Connected");
        Assert.Equal("", original.Console);
        Assert.Equal("RX audio disabled.", original.Audio);
        Assert.Equal("Listening", status.Snapshot.Latest);
        Assert.All(observed, snapshot => Assert.Equal("Listening", snapshot.Latest));
        int count = observed.Count;
        status.SetConsole("Connected");
        Assert.Equal(count, observed.Count);
        Assert.Equal("Listening", status.Snapshot.Latest);
    }
}
