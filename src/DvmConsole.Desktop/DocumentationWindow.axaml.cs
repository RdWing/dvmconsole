using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DvmConsole.Desktop;

public sealed partial class DocumentationWindow : Window
{
    private readonly DocumentationCatalog catalog;
    private CancellationTokenSource reloadCancellation = new();

    public DocumentationWindow()
        : this(DocumentationCatalog.OpenDefault())
    {
    }

    internal DocumentationWindow(DocumentationCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        InitializeComponent();
        markdownViewer.Markdown = "# Loading documentation\n\nFetching the current pages from GitHub…";
        Opened += HandleOpened;
        Closed += HandleClosed;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        searchBox = this.FindControl<TextBox>("searchBox")
            ?? throw new InvalidOperationException("Documentation search control was not created.");
        documentTree = this.FindControl<TreeView>("documentTree")
            ?? throw new InvalidOperationException("Documentation tree control was not created.");
        markdownViewer = this.FindControl<Markdown.Avalonia.MarkdownScrollViewer>("markdownViewer")
            ?? throw new InvalidOperationException("Documentation content control was not created.");
    }

    private async void HandleOpened(object? sender, EventArgs e)
        => await ReloadTreeAsync();

    private void HandleClosed(object? sender, EventArgs e)
    {
        reloadCancellation.Cancel();
        reloadCancellation.Dispose();
    }

    private async void HandleSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        reloadCancellation.Cancel();
        reloadCancellation.Dispose();
        reloadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = reloadCancellation.Token;
        try
        {
            await Task.Delay(250, cancellationToken);
            await ReloadTreeAsync(searchBox.Text, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void HandleDocumentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (documentTree.SelectedItem is not TreeViewItem { Tag: DocumentationPage page })
            return;

        markdownViewer.Markdown = $"# {page.Title}\n\nLoading the current page from GitHub…";
        try
        {
            string markdown = await catalog.ReadAsync(page, reloadCancellation.Token);
            if (documentTree.SelectedItem is TreeViewItem { Tag: DocumentationPage selected } && selected == page)
                markdownViewer.Markdown = markdown;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            markdownViewer.Markdown = FormatUnavailable(exception);
        }
    }

    private async Task ReloadTreeAsync(
        string? searchText = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DocumentationPage> pages;
        try
        {
            pages = await catalog.FindAsync(searchText, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            documentTree.ItemsSource = Array.Empty<object?>();
            markdownViewer.Markdown = FormatUnavailable(exception);
            return;
        }

        var roots = new List<object?>();
        var folders = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (DocumentationPage page in pages)
        {
            string? directory = Path.GetDirectoryName(page.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            IList<object?> parent = roots;
            string cumulative = string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                foreach (string segment in directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                {
                    cumulative = cumulative.Length == 0 ? segment : Path.Combine(cumulative, segment);
                    if (!folders.TryGetValue(cumulative, out TreeViewItem? folder))
                    {
                        folder = new TreeViewItem
                        {
                            Header = DocumentationCatalog.FormatTitle(segment),
                            IsExpanded = true,
                            ItemsSource = new List<object?>()
                        };
                        folders[cumulative] = folder;
                        parent.Add(folder);
                    }
                    parent = (IList<object?>)folder.ItemsSource!;
                }
            }

            parent.Add(new TreeViewItem { Header = page.Title, Tag = page });
        }

        documentTree.ItemsSource = roots;
        TreeViewItem? firstPage = FindFirstPage(roots);
        if (firstPage is null)
        {
            markdownViewer.Markdown = string.IsNullOrWhiteSpace(searchText)
                ? "# Documentation unavailable\n\nNo documentation pages are configured."
                : "# No results\n\nNo current GitHub documentation pages match the search.";
            return;
        }

        firstPage.IsSelected = true;
    }

    private static string FormatUnavailable(Exception exception)
        => "# Documentation unavailable\n\n" +
           "DVM Console reads these pages live from GitHub. Check the network connection and try again.\n\n" +
           $"`{exception.Message}`";

    private static TreeViewItem? FindFirstPage(IEnumerable<object?> items)
    {
        foreach (object? item in items)
        {
            if (item is not TreeViewItem treeItem)
                continue;
            if (treeItem.Tag is DocumentationPage)
                return treeItem;
            if (treeItem.ItemsSource is IEnumerable<object?> children && FindFirstPage(children) is { } page)
                return page;
        }
        return null;
    }
}
