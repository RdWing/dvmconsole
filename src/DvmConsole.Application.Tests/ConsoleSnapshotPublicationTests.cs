// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleSnapshotPublicationTests
{
    [Fact]
    public async Task OlderConcurrentCaptureMustNotOverwriteNewerRuntimeState()
    {
        var adapter = new PausingAdapter();
        await using var session = new ConsoleApplicationSession(adapter);
        Task older = Task.Run(adapter.Invalidate);
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            adapter.Status = "newer";
            adapter.Invalidate();
            Assert.Equal("newer", session.Snapshot.StatusText);
        }
        finally { adapter.Release.Set(); }
        await older.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("newer", session.Snapshot.StatusText);
    }

    [Fact]
    public async Task InFlightRuntimeCaptureMustPreserveCompletedQuiescence()
    {
        var adapter = new PausingAdapter();
        await using var session = new ConsoleApplicationSession(adapter);
        Task older = Task.Run(adapter.Invalidate);
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await session.QuiesceAsync(CancellationToken.None);
            Assert.True(session.Snapshot.IsQuiescing);
        }
        finally { adapter.Release.Set(); }
        await older.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Snapshot.IsQuiescing);
    }

    private sealed class PausingAdapter : IConsoleSessionRuntimeAdapter
    {
        public string Status = "older";
        private int captures;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public ConsoleTopologySnapshot CaptureTopology() => ConsoleTopologySnapshot.Empty;
        public ConsoleRuntimeSnapshot CaptureSnapshot() => ConsoleRuntimeSnapshot.Empty with { StatusText = Status };
        public ConsoleSnapshotUpdate CaptureUpdate(ConsoleRuntimeSnapshot previous)
        {
            var captured = CaptureSnapshot();
            if (Interlocked.Increment(ref captures) == 1)
            {
                Entered.SetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            return new(captured, []);
        }
        public void Invalidate() => ControlStateInvalidated?.Invoke(this, EventArgs.Empty);
        public IReadOnlyList<ConsoleCallHistoryRecord> History => [];
        public IConsoleCommands Commands { get; } = new NoOpConsoleCommands();
        public event EventHandler? ControlStateInvalidated;
        public event EventHandler<ChannelMeterSample>? MeterSampled { add { } remove { } }
        public event EventHandler<ConsoleLogEvent>? LogPublished { add { } remove { } }
        public ValueTask QuiesceAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask FlushSettingsAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Release.Dispose(); return ValueTask.CompletedTask; }
    }
}
