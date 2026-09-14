// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Headless.XUnit;
using DvmConsole.Application;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileSessionAttachmentTests
{
    [AvaloniaFact]
    public async Task StopAdmissionClosesRuntimeBeforeReturningToUiCleanup()
    {
        var probe = new AdmissionProbe();
        var application = new ConsoleApplicationSession(probe);
        var view = new MobileConsoleView(ConsoleHostFormFactor.Phone,
            applicationSession: application, ownsSession: false);
        var attachment = new MobileSessionAttachment(view, new(application));
        try
        {
            attachment.StopAdmission();
            Assert.True(probe.Suspended);
        }
        finally { await attachment.DisposeOwnerAsync(); }
    }

    private sealed class AdmissionProbe : IConsoleSessionRuntimeAdapter, IConsoleSessionInputAdmission
    {
        public bool Suspended { get; private set; }
        public void SuspendInput() => Suspended = true;
        public ConsoleTopologySnapshot CaptureTopology() => new(null, [], [], []);
        public ConsoleRuntimeSnapshot CaptureSnapshot() => ConsoleRuntimeSnapshot.Empty;
        public IReadOnlyList<ConsoleCallHistoryRecord> History => [];
        public IConsoleCommands Commands { get; } = new NoOpConsoleCommands();
        public event EventHandler? ControlStateInvalidated { add { } remove { } }
        public event EventHandler<ChannelMeterSample>? MeterSampled { add { } remove { } }
        public event EventHandler<ConsoleLogEvent>? LogPublished { add { } remove { } }
        public ValueTask QuiesceAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask FlushSettingsAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public async Task SettingsResumeRetriesInitialActivationAndThenUsesRecovery()
    {
        await using var application = new ConsoleApplicationSession(new ConsoleTopologySnapshot(null, [], [], []),
            ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
        await using var view = new MobileConsoleView(ConsoleHostFormFactor.Phone,
            applicationSession: application, ownsSession: false);
        int attempts = 0, recoveries = 0;
        var attachment = new MobileSessionAttachment(view, new(application,
            ActivateListening: _ => ++attempts == 1 ? Task.FromException(new IOException("Unavailable")) : Task.CompletedTask,
            ResumeListening: _ => { recoveries++; return Task.CompletedTask; }));
        try
        {
            await Assert.ThrowsAsync<IOException>(attachment.RestorePlaybackAsync);
            await attachment.Session.ResumeListening!(CancellationToken.None);
            await attachment.RestorePlaybackAsync();
            await attachment.Session.ResumeListening(CancellationToken.None);
            Assert.Equal(2, attempts);
            Assert.Equal(1, recoveries);
        }
        finally { await attachment.DisposeOwnerAsync(); }
    }

    [AvaloniaFact]
    public async Task RetiringRuntimeDoesNotWaitForCancellationIgnoringActivation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool runtimeRetired = false;
        await using var application = new ConsoleApplicationSession(new ConsoleTopologySnapshot(null, [], [], []),
            ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands(),
            dispose: () => { runtimeRetired = true; return ValueTask.CompletedTask; });
        await using var view = new MobileConsoleView(ConsoleHostFormFactor.Phone,
            applicationSession: application, ownsSession: false);
        var attachment = new MobileSessionAttachment(view, new(application, ActivateListening: _ => release.Task));
        Task activation = attachment.RestorePlaybackAsync();
        Task retirement = attachment.DisposeSessionAsync().AsTask();
        try { Assert.True(runtimeRetired); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);
        await retirement;
        await attachment.DisposeOwnerAsync();
    }
}
