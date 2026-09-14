// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Presentation;
using Markdown.Avalonia;

namespace DvmConsole.Mobile;

/// <summary>Touch navigation over the same packaged documentation used by Desktop.</summary>
internal sealed class MobileHelpView : UserControl
{
    public event EventHandler? SettingsRequested;
    private readonly Func<IConsoleHelpCatalog> openCatalog;
    private readonly bool tablet;
    private readonly Grid root = new() { Margin = new Thickness(12), ColumnSpacing = 12 };
    private readonly Grid index = new() { RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 8 };
    private readonly Grid article = new() { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8 };
    private readonly TextBox search = new() { Watermark = "Search help", MinHeight = 44 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock articleStatus = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    internal sealed record HelpRow(string Title, ConsoleHelpTopic? Topic)
    { public bool IsTopic => Topic is not null; }
    private readonly ListBox topics = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly MarkdownScrollViewer reader = new()
    {
        MarkdownStyleName = "FluentTheme",
        Markdown = "# Console NEO Help\n\nChoose a topic to open the guide."
    };
    private readonly Button backToTopics = new() { Content = "‹ Help topics", MinHeight = 44 };
    private IConsoleHelpCatalog? catalog;
    private ConsoleHelpTopic? selectedTopic;
    private CancellationTokenSource? lifetime;
    private CancellationTokenSource? searchCancellation;
    private CancellationTokenSource? readCancellation;
    private bool showingArticle;
    private bool? wideLayout;

    public MobileHelpView(Func<IConsoleHelpCatalog> openCatalog, ConsoleHostFormFactor formFactor)
    {
        this.openCatalog = openCatalog;
        tablet = formFactor == ConsoleHostFormFactor.Tablet;
        var back = new Button { Content = "‹ Settings", MinHeight = 44 };
        AutomationProperties.SetName(back, "Back to Settings");
        back.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var heading = MobileSettingsPageLayout.Heading("Help", back);
        index.Children.Add(heading);
        AutomationProperties.SetName(search, "Search help");
        Grid.SetRow(search, 1); index.Children.Add(search);
        Grid.SetRow(status, 2); index.Children.Add(status);
        topics.ItemTemplate = new FuncDataTemplate<HelpRow>((row, _) => new TextBlock
        {
            Text = row?.Title,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = row?.Topic is null ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = row?.Topic is null ? new Thickness(12, 10, 12, 4) : new Thickness(12, 6)
        });
        AutomationProperties.SetName(topics, "Help topics");
        topics.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
            new Setter(Layoutable.MinHeightProperty, 44d),
            new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
            new Setter(Layoutable.MarginProperty, new Thickness(0, 1)) }
        });
        topics.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Template().OfType<ContentPresenter>())
        { Setters = { new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(8)) } });
        topics.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("OperationalSelectionSurfaceBrush")) }
        });
        topics.ContainerPrepared += (_, args) =>
            args.Container.IsEnabled = topics.Items[args.Index] is HelpRow { IsTopic: true };
        ScrollViewer.SetHorizontalScrollBarVisibility(topics, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Grid.SetRow(topics, 3); index.Children.Add(topics);
        backToTopics.Click += (_, _) => ShowTopics();
        article.Children.Add(backToTopics);
        Grid.SetRow(articleStatus, 1); article.Children.Add(articleStatus);
        Grid.SetRow(reader, 2); article.Children.Add(reader);
        if (reader.Engine is Markdown.Avalonia.Markdown engine)
            engine.HyperlinkCommand = new LinkCommand(OpenLinkAsync);
        reader.Classes.Add("help-reader");
        reader.Styles.Add(new Style(selector => selector.OfType<MarkdownScrollViewer>().Class("help-reader").Descendant().OfType<ColorTextBlock.Avalonia.CTextBlock>())
        {
            Setters = { new Setter(MarginProperty, new Thickness(0, 9)),
                new Setter(ColorTextBlock.Avalonia.CTextBlock.LineSpacingProperty, 4d),
                new Setter(ColorTextBlock.Avalonia.CTextBlock.FontSizeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("MobileBodyFontSize")) }
        });
        for (int level = 1; level <= 4; level++)
            reader.Styles.Add(new Style(selector => selector.OfType<MarkdownScrollViewer>().Class("help-reader").Descendant().OfType<ColorTextBlock.Avalonia.CTextBlock>().Class("Heading" + level))
            {
                Setters = { new Setter(MarginProperty, new Thickness(0, 22, 0, 10)),
                new Setter(ColorTextBlock.Avalonia.CTextBlock.FontSizeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("MobileHelpHeading" + Math.Min(level, 3) + "FontSize")),
                new Setter(ColorTextBlock.Avalonia.CTextBlock.FontWeightProperty, FontWeight.SemiBold) }
            });
        reader.Styles.Add(new Style(selector => selector.OfType<MarkdownScrollViewer>().Class("help-reader").Descendant().OfType<ColorTextBlock.Avalonia.CTextBlock>().Class("ListMarker"))
        { Setters = { new Setter(MarginProperty, new Thickness(0, 9, 5, 9)) } });
        root.Children.Add(index); root.Children.Add(article);
        Content = root;
        SizeChanged += (_, _) => UpdateLayoutMode();
        search.TextChanged += async (_, _) => await SearchAsync(debounce: true);
        topics.SelectionChanged += async (_, _) =>
        {
            if (topics.SelectedItem is HelpRow { Topic: { } topic }) await ReadAsync(topic);
        };
        AttachedToVisualTree += async (_, _) => await OpenAsync();
        DetachedFromVisualTree += (_, _) =>
        {
            Cancel(ref lifetime);
            Cancel(ref searchCancellation);
            Cancel(ref readCancellation);
        };
        UpdateLayoutMode();
    }

    private async Task OpenAsync()
    {
        lifetime = new();
        var owner = lifetime;
        try
        {
            status.Text = "Opening guide…";
            var opened = catalog ?? await Task.Run(openCatalog, owner.Token);
            if (!ReferenceEquals(owner, lifetime)) return;
            catalog = opened;
            await SearchAsync(debounce: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(owner, lifetime)) status.Text = $"Help unavailable: {exception.Message}";
        }
    }

    private async Task SearchAsync(bool debounce)
    {
        if (catalog is null || lifetime is null) return;
        Cancel(ref searchCancellation);
        var request = searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        string query = search.Text ?? string.Empty;
        try
        {
            if (debounce) await Task.Delay(250, request.Token);
            var found = await catalog.FindAsync(query, request.Token);
            if (!ReferenceEquals(request, searchCancellation)) return;
            var rows = new List<HelpRow>();
            foreach (var section in found.GroupBy(topic => topic.Section))
            {
                string label = section.Key.Replace("Getting Started", "", StringComparison.OrdinalIgnoreCase)
                    .Trim(' ', '/', '›');
                if (label.Length > 0) rows.Add(new HelpRow(label, null));
                rows.AddRange(section.Select(topic => new HelpRow(topic.Title, topic)));
            }
            topics.ItemsSource = rows;
            status.Text = found.Count == 0 ? "No matching help topics." : string.Empty;
            status.IsVisible = found.Count == 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(request, searchCancellation))
            { status.Text = $"Search unavailable: {exception.Message}"; status.IsVisible = true; }
        }
    }

    internal async Task ReadAsync(ConsoleHelpTopic topic, string? fragment = null)
    {
        if (catalog is null || lifetime is null) return;
        Cancel(ref readCancellation);
        var request = readCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        selectedTopic = topic;
        showingArticle = true;
        articleStatus.IsVisible = false;
        reader.ScrollValue = default;
        reader.Markdown = $"# {topic.Title}\n\nLoading…";
        UpdateLayoutMode();
        // Leave the search field so the software keyboard does not cover the article.
        if (search.IsKeyboardFocusWithin) TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        try
        {
            string markdown = await catalog.ReadAsync(topic.Id, request.Token);
            if (ReferenceEquals(request, readCancellation))
            {
                reader.Markdown = markdown;
                if (fragment is not null) await ScrollToHeadingAsync(fragment, request);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(request, readCancellation)) reader.Markdown = $"# Help unavailable\n\n{exception.Message}";
        }
    }

    internal async Task OpenLinkAsync(string link)
    {
        var owner = readCancellation;
        if (lifetime is null || owner is null || selectedTopic is null) return;
        try
        {
            if (Uri.TryCreate(link, UriKind.Absolute, out var external))
            {
                if (external.Scheme is "https" or "http" && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                {
                    if (await launcher.LaunchUriAsync(external)) return;
                }
                ShowLinkError("This link could not be opened.", owner);
            }
            else if (selectedTopic is { } current && catalog?.ResolveLink(current.Id, link) is { } destination)
            {
                int separator = link.IndexOf('#');
                string? fragment = separator < 0 ? null : link[(separator + 1)..];
                if (destination.Id == current.Id && fragment is not null)
                    await ScrollToHeadingAsync(fragment, owner);
                else await ReadAsync(destination, fragment);
            }
            else ShowLinkError("This topic is not in the bundled guide.", owner);
        }
        catch (Exception exception)
        { ShowLinkError($"Link unavailable: {exception.Message}", owner); }
    }

    private void ShowLinkError(string message, CancellationTokenSource owner)
    {
        if (!ReferenceEquals(owner, readCancellation)) return;
        articleStatus.Text = message;
        articleStatus.IsVisible = true;
    }

    private async Task ScrollToHeadingAsync(string fragment, CancellationTokenSource owner)
    {
        // The Markdown control rebuilds its visual tree when content changes.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        if (!ReferenceEquals(owner, readCancellation)) return;
        articleStatus.IsVisible = false;
        if (!HelpHeadingNavigation.ScrollToHeading(reader, fragment))
            ShowLinkError("This heading is not in the topic.", owner);
    }

    private void ShowTopics()
    {
        Cancel(ref readCancellation);
        selectedTopic = null;
        showingArticle = false;
        topics.SelectedItem = null;
        UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        bool wide = tablet && Bounds.Width >= 800;
        if (wideLayout != wide)
        {
            wideLayout = wide;
            root.ColumnDefinitions = new(wide ? "280,*" : "*");
            Grid.SetColumn(article, wide ? 1 : 0);
        }
        index.IsVisible = wide || !showingArticle;
        article.IsVisible = wide || showingArticle;
        backToTopics.IsVisible = !wide;
    }

    private static void Cancel(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }

    private sealed class LinkCommand(Func<string, Task> open) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => parameter is string;
        public async void Execute(object? parameter) { if (parameter is string link) await open(link); }
    }
}
