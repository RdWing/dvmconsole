// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class RecordingHistoryTests
{
    [Fact]
    public void RecordingRowsShareFiltersAndReturnToPlayAfterPlaybackEnds()
    {
        var record = Recording();
        var model = new RecordingHistoryViewModel();
        model.Refresh([record], _ => false);
        var row = Assert.Single(model.FilteredCallHistory);
        Assert.Equal("Play", row.RecordingPlaybackActionText);
        model.RefreshPlayback(_ => true);
        Assert.Equal("Stop", row.RecordingPlaybackActionText);
        model.RefreshPlayback(_ => false);
        Assert.Equal("Play", row.RecordingPlaybackActionText);
        model.RecordingEncryptionFilter = "Encrypted";
        Assert.Empty(model.FilteredCallHistory);
        model.Refresh([record with { EncryptionState = CallRecordingEncryptionState.Secure, EncryptionText = "AES" }], _ => false);
        Assert.Same(row, Assert.Single(model.FilteredCallHistory));
        model.CallHistoryFilterText = "Engine";
        Assert.Single(model.FilteredCallHistory);
        model.CallHistoryFilterText = "Missing";
        Assert.Empty(model.FilteredCallHistory);
    }

    [AvaloniaTheory]
    [InlineData(390, 700, false)]
    [InlineData(390, 700, true)]
    [InlineData(852, 320, false)]
    [InlineData(852, 320, true)]
    [InlineData(1024, 768, false)]
    [InlineData(1024, 768, true)]
    public async Task MobileArchiveShowsTouchActionsAndDeletesByRecordingIdentity(double width, double height, bool sessionHistory)
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var record = Recording();
        var archive = new Archive(record);
        var session = new MobileSession(new HistorySession(console.ApplicationSession, sessionHistory), RecordingArchive: archive);
        var view = new MobileHistoryView(() => session);
        var window = new Window { Width = width, Height = height, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<ComboBox>(), combo => combo.Items.Contains("Recordings"));
            window.UpdateLayout();
            var history = view.GetVisualDescendants().OfType<CallHistoryView>().Single();
            object row = Assert.Single(history.HistoryItems.Items)!;
            var attached = sessionHistory ? Assert.IsType<ConsoleCallHistoryItemViewModel>(row).Recording
                : Assert.IsType<RecordingHistoryItemViewModel>(row).Recording;
            Assert.Equal(record.Id, attached!.Id);
            Assert.True(history.ClearButton.IsVisible);
            var actions = history.GetVisualDescendants().OfType<Button>().Where(button => ReferenceEquals(button.Tag, row)).ToArray();
            Assert.Equal(3, actions.Length);
            Assert.All(actions, button => Assert.True(button.Bounds.Height >= 44));
            Assert.Contains(actions, button => Equals(button.Content, "Export"));
            actions.Single(button => Equals(button.Content, "Delete")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Null(archive.Deleted);
            view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Confirm?"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Equal(record.Id, archive.Deleted);
            if (sessionHistory)
                Assert.False(Assert.IsType<ConsoleCallHistoryItemViewModel>(Assert.Single(history.HistoryItems.Items)).HasRecording);
            else Assert.Empty(history.HistoryItems.Items);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DeleteConfirmationIsClearedByNavigationAndCannotCrossSessions()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var recording = Recording();
        var archive = new Archive(recording);
        var current = new MobileSession(new HistorySession(console.ApplicationSession, false), RecordingArchive: archive);
        var view = new MobileHistoryView(() => current);
        var window = new Window { Width = 390, Height = 700, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            Button Delete() => view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Delete"));
            Button first = Delete();
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Confirm?", first.Content);
            window.Content = null;
            Assert.Equal("Delete", first.Content);
            Assert.Null(archive.Deleted);
            window.Content = view; window.UpdateLayout();
            Button second = Delete();
            second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            current = new MobileSession(new HistorySession(console.ApplicationSession, false), RecordingArchive: archive);
            second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(archive.Deleted);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingPlayResumesPausedAudioAndRejectsSessionReplacement(bool replace)
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        await using var other = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var execution = new ConsoleExecutionPolicy();
        execution.MediaServicesReset();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int resumes = 0;
        MobileSession current = new(console.ApplicationSession, ResumeListening: async _ =>
        {
            resumes++;
            await resume.Task;
            execution.CompleteRecovery(execution.BeginRecovery(explicitResume: true)!.Value, true);
        })
        { Execution = execution };
        var commands = new PlaybackCommands();
        var view = new MobileHistoryView(() => current);
        var id = new RecordingId(Guid.NewGuid());
        var playing = view.PlayRecordingAsync(current, commands, id);
        Assert.Equal(1, resumes);
        Assert.Null(commands.Played);
        if (replace) current = new(other.ApplicationSession);
        resume.SetResult();
        if (replace)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => playing);
            Assert.Null(commands.Played);
        }
        else
        {
            await playing;
            Assert.Equal(id, commands.Played);
            await view.PlayRecordingAsync(current, commands, id);
            Assert.Equal(1, resumes);
        }
    }

    private sealed class PlaybackCommands : IConsoleRecordingPlaybackCommands
    {
        public RecordingId? Played;
        public bool IsRecordingPlaying(RecordingId id) => Played == id;
        public ValueTask PlayRecordingAsync(RecordingId id, CancellationToken cancellationToken = default)
        { Played = id; return ValueTask.CompletedTask; }
        public ValueTask StopRecordingPlaybackAsync(CancellationToken cancellationToken = default)
        { Played = null; return ValueTask.CompletedTask; }
    }

    private static RecordingArchiveEntry Recording() => new(new RecordingId(Guid.NewGuid()), DateTimeOffset.UnixEpoch,
        TimeSpan.FromSeconds(2), "North", "Dispatch", "RX", "P25", "42", "100", "Engine 42", "42 → TG 100",
        [10], CallRecordingEncryptionState.Clear, "Clear", true, "call.opus", "Dispatch recording")
    { CallIdentity = new(DateTimeOffset.UnixEpoch, "North", "Dispatch", "RX", "P25", 42, 100, 10, null) };

    private sealed class HistorySession(IConsoleApplicationSession inner, bool includeSessionHistory) : IConsoleApplicationSession
    {
        public ConsoleSessionId Id => inner.Id;
        public ConsoleTopologySnapshot Topology => inner.Topology;
        public ConsoleRuntimeSnapshot Snapshot => inner.Snapshot;
        public IConsoleCommands Commands => inner.Commands;
        public IReadOnlyList<ConsoleCallHistoryRecord> History { get; } = !includeSessionHistory ? [] :
            [new(CallId.New(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(2),
                SystemId.FromName("North"), "North", null, "Dispatch", RadioMediaProtocol.P25, 42, 100,
                10, [10], null, "Engine 42", ConsoleCallDirection.Receive, RecordingEncryptionDescriptor.Clear, "", "", "", "")];
        public event EventHandler<ConsoleSnapshotChangedEventArgs>? SnapshotChanged { add { } remove { } }
        public event EventHandler<ChannelMeterSample>? MeterSampled { add { } remove { } }
        public event EventHandler<ConsoleLogEvent>? LogPublished { add { } remove { } }
        public ValueTask QuiesceAsync(CancellationToken token) => inner.QuiesceAsync(token);
        public ValueTask FlushSettingsAsync(CancellationToken token) => inner.FlushSettingsAsync(token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Archive(RecordingArchiveEntry recording) : IConsoleRecordingArchive
    {
        public RecordingId? Deleted { get; private set; }
        public Task<IReadOnlyList<RecordingArchiveEntry>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RecordingArchiveEntry>>(Deleted is null ? [recording] : []);
        public Task ExportAsync(RecordingId id, Stream destination, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<bool> DeleteAsync(RecordingId id, CancellationToken cancellationToken = default)
        { Deleted = id; return Task.FromResult(true); }
    }
}
