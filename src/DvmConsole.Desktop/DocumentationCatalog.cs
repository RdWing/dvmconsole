using System.Text.RegularExpressions;

namespace DvmConsole.Desktop;

internal sealed record DocumentationPage(string Title, string RelativePath, string FilePath);

internal sealed class DocumentationCatalog
{
    private static readonly Regex SortPrefix = new(@"^\d+\s*-\s*", RegexOptions.Compiled);
    private readonly string root;

    public DocumentationCatalog(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
    }

    public string Root => root;

    public static DocumentationCatalog OpenDefault()
    {
        foreach (string candidate in EnumerateRootCandidates(AppContext.BaseDirectory))
        {
            if (Directory.Exists(candidate))
                return new DocumentationCatalog(candidate);
        }

        throw new DirectoryNotFoundException(
            $"The documentation folder was not found. Expected Docs under {AppContext.BaseDirectory}.");
    }

    internal static IEnumerable<string> EnumerateRootCandidates(string baseDirectory)
    {
        string current = Path.GetFullPath(baseDirectory);
        for (int depth = 0; depth < 8; depth++)
        {
            yield return Path.Combine(current, "Docs");
            yield return Path.Combine(current, "dvmconsole", "Docs");
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null)
                yield break;
            current = parent.FullName;
        }
    }

    public IReadOnlyList<DocumentationPage> Find(string? searchText = null)
    {
        string query = searchText?.Trim() ?? string.Empty;
        var pages = new List<DocumentationPage>();
        foreach (string filePath in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, filePath);
            string title = FormatTitle(Path.GetFileName(filePath));
            if (query.Length > 0)
            {
                string markdown;
                try
                {
                    markdown = File.ReadAllText(filePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (!title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !markdown.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            pages.Add(new DocumentationPage(title, relativePath, Path.GetFullPath(filePath)));
        }

        return pages
            .OrderBy(page => page.RelativePath, DocumentationPathComparer.Instance)
            .ToArray();
    }

    public string Read(DocumentationPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        string fullPath = Path.GetFullPath(page.FilePath);
        string relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("The documentation page is outside the configured documentation folder.");
        return File.ReadAllText(fullPath);
    }

    public static string FormatTitle(string value)
    {
        string name = Path.GetFileNameWithoutExtension(value ?? string.Empty);
        return SortPrefix.Replace(name, string.Empty).Trim();
    }

    private sealed class DocumentationPathComparer : IComparer<string>
    {
        public static DocumentationPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            string[] leftParts = (left ?? string.Empty).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string[] rightParts = (right ?? string.Empty).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int common = Math.Min(leftParts.Length, rightParts.Length);
            for (int index = 0; index < common; index++)
            {
                int result = StringComparer.OrdinalIgnoreCase.Compare(leftParts[index], rightParts[index]);
                if (result != 0)
                    return result;
            }
            return leftParts.Length.CompareTo(rightParts.Length);
        }
    }
}
