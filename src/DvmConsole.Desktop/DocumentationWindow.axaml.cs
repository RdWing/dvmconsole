using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DvmConsole.Desktop;

public sealed partial class DocumentationWindow : Window
{
    private readonly DocumentationCatalog catalog;

    public DocumentationWindow()
        : this(DocumentationCatalog.OpenDefault())
    {
    }

    internal DocumentationWindow(DocumentationCatalog catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        InitializeComponent();
        ReloadTree();
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

    private void HandleSearchTextChanged(object? sender, TextChangedEventArgs e)
        => ReloadTree(searchBox.Text);

    private void HandleDocumentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (documentTree.SelectedItem is not TreeViewItem { Tag: DocumentationPage page })
            return;

        try
        {
            markdownViewer.Markdown = catalog.Read(page);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            markdownViewer.Markdown = $"# Documentation unavailable\n\n{exception.Message}";
        }
    }

    private void ReloadTree(string? searchText = null)
    {
        IReadOnlyList<DocumentationPage> pages;
        try
        {
            pages = catalog.Find(searchText);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            documentTree.ItemsSource = Array.Empty<object?>();
            markdownViewer.Markdown = $"# Documentation unavailable\n\n{exception.Message}";
            return;
        }

        var roots = new List<object?>();
        var folders = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (DocumentationPage page in pages)
        {
            string? directory = Path.GetDirectoryName(page.RelativePath);
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
                ? "# Documentation unavailable\n\nNo Markdown pages were found."
                : "# No results\n\nNo documentation pages match the current search.";
            return;
        }

        firstPage.IsSelected = true;
    }

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
