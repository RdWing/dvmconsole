// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Markdown.Avalonia;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileHelpTests
{
    [AvaloniaFact]
    public async Task HeadingLinksScrollWithinAndAcrossTopicsWithoutReloadingTheCurrentArticle()
    {
        var catalog = new HeadingCatalog();
        var help = new MobileHelpView(() => catalog, ConsoleHostFormFactor.Phone);
        var window = new Window { Width = 390, Height = 600, Content = help };
        try
        {
            window.Show();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var topics = help.GetVisualDescendants().OfType<ListBox>().Single();
            while (topics.Items.Count == 0) await Task.Delay(10, timeout.Token);
            await help.ReadAsync(HeadingCatalog.First);
            var reader = help.GetVisualDescendants().OfType<MarkdownScrollViewer>().Single();
            await help.OpenLinkAsync("#receive-audio");
            window.UpdateLayout();
            var headingText = reader.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>()
                .First(text => text.Classes.Contains("Heading1"));
            Assert.Equal(22, headingText.Margin.Top);
            Assert.Equal(4, headingText.LineSpacing);
            double firstOffset = reader.ScrollValue.Y;
            Assert.True(firstOffset > 100);
            await help.OpenLinkAsync("#receive-audio-1");
            window.UpdateLayout();
            Assert.True(reader.ScrollValue.Y > firstOffset);
            Assert.Equal(1, catalog.Reads);
            await help.OpenLinkAsync("second.md#caf%C3%A9");
            window.UpdateLayout();
            Assert.Equal(2, catalog.Reads);
            Assert.Contains("Second topic", reader.Markdown);
            Assert.True(reader.ScrollValue.Y > 100);
            await help.OpenLinkAsync("#missing-heading");
            Assert.Contains(help.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.IsEffectivelyVisible && text.Text == "This heading is not in the topic.");
            await help.OpenLinkAsync("#");
            window.UpdateLayout();
            Assert.Equal(0, reader.ScrollValue.Y);
        }
        finally { window.Close(); }
    }

    private sealed class HeadingCatalog : IConsoleHelpCatalog
    {
        public static readonly ConsoleHelpTopic First = new("first.md", "First topic", "Guide");
        private static readonly ConsoleHelpTopic Second = new("second.md", "Second topic", "Guide");
        private static readonly string Paragraphs = string.Concat(Enumerable.Repeat("Some guide text.\n\n", 35));
        public int Reads { get; private set; }
        public Task<IReadOnlyList<ConsoleHelpTopic>> FindAsync(string? searchText = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConsoleHelpTopic>>([First, Second]);
        public Task<string> ReadAsync(string topicId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(topicId == First.Id
                ? "# First topic\n\n" + Paragraphs + "## Receive **audio**\n\n" + Paragraphs + "## Receive audio\n\n" + Paragraphs
                : "# Second topic\n\n" + Paragraphs + "## Café\n\n" + Paragraphs);
        }
        public ConsoleHelpTopic? ResolveLink(string topicId, string link)
            => link.StartsWith("second.md", StringComparison.Ordinal) || topicId == Second.Id ? Second : First;
    }

    [AvaloniaFact]
    public async Task ArticleShowsLinkFailureAndBackCancelsPendingRead()
    {
        var catalog = new DeferredCatalog();
        var help = new MobileHelpView(() => catalog, ConsoleHostFormFactor.Phone);
        var window = new Window { Width = 390, Height = 844, Content = help };
        try
        {
            window.Show();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var topics = help.GetVisualDescendants().OfType<ListBox>().Single();
            while (topics.Items.Count == 0) await Task.Delay(10, timeout.Token);
            Task read = help.ReadAsync(topics.Items.OfType<MobileHelpView.HelpRow>().First(row => row.Topic is not null).Topic!);
            await catalog.ReadStarted.Task.WaitAsync(timeout.Token);
            await help.OpenLinkAsync("missing.md");
            window.UpdateLayout();
            Assert.Contains(help.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.IsEffectivelyVisible && text.Text == "This topic is not in the bundled guide.");
            var back = help.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "‹ Help topics"));
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(catalog.ReadToken.IsCancellationRequested);
            catalog.Content.TrySetResult("# Late article after Back");
            await read.WaitAsync(timeout.Token);
            var reader = help.GetVisualDescendants().OfType<MarkdownScrollViewer>().Single();
            Assert.False(reader.IsEffectivelyVisible);
            Assert.True(topics.IsEffectivelyVisible);
            Assert.DoesNotContain("Late article after Back", reader.Markdown);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(390, 844, ConsoleHostFormFactor.Phone)]
    [InlineData(852, 390, ConsoleHostFormFactor.Phone)]
    [InlineData(1024, 768, ConsoleHostFormFactor.Tablet)]
    public async Task HelpFitsAndDiscardsLateContentAfterNavigation(double width, double height, ConsoleHostFormFactor form)
    {
        var catalog = new DeferredCatalog();
        var help = new MobileHelpView(() => catalog, form);
        var window = new Window { Width = width, Height = height, Content = help };
        try
        {
            window.Show();
            var topics = help.GetVisualDescendants().OfType<ListBox>().Single();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (topics.Items.Count == 0) await Task.Delay(10, timeout.Token);
            Task read = help.ReadAsync(topics.Items.OfType<MobileHelpView.HelpRow>().First(row => row.Topic is not null).Topic!);
            await catalog.ReadStarted.Task.WaitAsync(timeout.Token);
            window.UpdateLayout();
            var reader = help.GetVisualDescendants().OfType<MarkdownScrollViewer>().Single();
            Assert.True(reader.IsEffectivelyVisible);
            Assert.True(reader.Bounds.Width > 100 && reader.Bounds.Height > 100);
            Assert.All(help.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible),
                button => Assert.True(button.Bounds.Height >= 44));
            window.Content = new Border();
            catalog.Content.TrySetResult("# Late retired article");
            await read.WaitAsync(timeout.Token);
            Assert.DoesNotContain("Late retired article", reader.Markdown);
        }
        finally { window.Close(); }
    }

    private sealed class DeferredCatalog : IConsoleHelpCatalog
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Content { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadToken { get; private set; }
        public Task<IReadOnlyList<ConsoleHelpTopic>> FindAsync(string? searchText = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConsoleHelpTopic>>([new("overview", "Console operation and configuration", "Getting started")]);
        public Task<string> ReadAsync(string topicId, CancellationToken cancellationToken = default)
        { ReadToken = cancellationToken; ReadStarted.TrySetResult(); return Content.Task; }
        public ConsoleHelpTopic? ResolveLink(string topicId, string link) => null;
    }
}
