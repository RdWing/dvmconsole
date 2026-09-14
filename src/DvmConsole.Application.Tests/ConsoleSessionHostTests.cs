// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleSessionHostTests
{
    [Fact]
    public async Task ReplacementWaitsForQuiescenceAndRetiresOnlyAfterPublication()
    {
        var events = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = new Attachment("old", events, async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        var replacement = new Attachment("new", events);
        await using var host = new ConsoleSessionHost(previous,
            attachment => events.Add(((Attachment)attachment).Name + ".publish"), () => { }, () => { });
        Task transition = host.ReplaceAsync(replacement);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(previous, host.Active);
            Assert.DoesNotContain("new.publish", events);
        }
        finally { release.TrySetResult(); }
        await transition.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(replacement, host.Active);
        Assert.True(events.IndexOf("old.stop-admission") < events.IndexOf("old.quiesce"));
        Assert.True(events.IndexOf("old.quiesce") < events.IndexOf("new.publish"));
        Assert.True(events.IndexOf("new.publish") < events.IndexOf("old.dispose-owner"));
        Assert.Contains("new.restore-playback", events);
    }

    [Fact]
    public async Task FailedPublicationDisposesReplacementAndRestoresPreviousConnections()
    {
        var events = new List<string>();
        var previous = new Attachment("old", events);
        var replacement = new Attachment("new", events);
        await using var host = new ConsoleSessionHost(previous, attachment =>
        {
            if (ReferenceEquals(attachment, replacement)) throw new InvalidOperationException("binding failed");
        }, () => { }, () => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReplaceAsync(replacement));
        Assert.Same(previous, host.Active);
        Assert.Contains("new.dispose-owner", events);
        Assert.Contains("old.restore-connections", events);
        Assert.Contains("old.resume-admission", events);
        Assert.DoesNotContain("old.dispose-owner", events);
    }

    private sealed class Attachment(string name, List<string> events,
        Func<CancellationToken, Task>? quiesce = null) : IConsoleSessionAttachment
    {
        private ConsoleApplicationSession? session;
        public string Name => name;
        public IConsoleApplicationSession ApplicationSession => session!;
        public void InitializeBindings()
        {
            events.Add(name + ".initialize");
            session = new(new ConsoleTopologySnapshot(null, [], [], []), ConsoleRuntimeSnapshot.Empty,
                new NoOpConsoleCommands(), quiesce: async token =>
                {
                    events.Add(name + ".quiesce");
                    if (quiesce is not null) await quiesce(token);
                });
        }
        public void DetachBindings() => events.Add(name + ".detach");
        public Task PrepareAsync(CancellationToken token) { events.Add(name + ".prepare"); return Task.CompletedTask; }
        public Task StartManualInputsAsync(CancellationToken token) { events.Add(name + ".start-manual"); return Task.CompletedTask; }
        public Task StopManualInputsAsync(CancellationToken token) { events.Add(name + ".stop-manual"); return Task.CompletedTask; }
        public void StopAdmission() => events.Add(name + ".stop-admission");
        public void ResumeAdmission() => events.Add(name + ".resume-admission");
        public void SuppressLiveOutput() => events.Add(name + ".suppress-output");
        public IReadOnlyList<SystemId> CaptureActiveSystemIds() => [SystemId.FromName("Test")];
        public ValueTask RestoreConnectionsAsync(IReadOnlyList<SystemId> systems, CancellationToken token)
        { events.Add(name + ".restore-connections"); return ValueTask.CompletedTask; }
        public Task RestorePlaybackAsync() { events.Add(name + ".restore-playback"); return Task.CompletedTask; }
        public ValueTask DisposeSessionAsync() => session?.DisposeAsync() ?? ValueTask.CompletedTask;
        public ValueTask DisposeManualControlsAsync() { events.Add(name + ".dispose-manual"); return ValueTask.CompletedTask; }
        public ValueTask DisposeOwnerAsync() { events.Add(name + ".dispose-owner"); return ValueTask.CompletedTask; }
    }
}
