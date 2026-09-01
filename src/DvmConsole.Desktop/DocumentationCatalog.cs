using System.Text.RegularExpressions;

namespace DvmConsole.Desktop;

internal sealed record DocumentationPage(
    string Title,
    string RelativePath,
    string FilePath);

internal sealed class DocumentationCatalog
{
    private static readonly Regex SortPrefixRegex = new(@"^\d+\s*-\s*", RegexOptions.Compiled);
    private static readonly string[] DefaultPagePaths =
    [
        "Getting Started/01-Overview.md",
        "Getting Started/02-Building.md",
        "Getting Started/03-Configurations/01-Codeplug Creation.md",
        "Getting Started/03-Configurations/02-Encryption Keys.md",
        "Getting Started/03-Configurations/03-RID Aliases.md",
        "Getting Started/03-Configurations/04-Groups and Patching.md",
        "Getting Started/03-Configurations/05-Talkgroup Audio Recorder.md",
        "Getting Started/03-Configurations/06-Configuration Troubleshooting.md",
        "Getting Started/04-Operations/01-Console Operation.md",
        "Getting Started/04-Operations/02-Settings Reference.md",
        "Getting Started/04-Operations/03-Audio Settings.md",
        "Getting Started/04-Operations/04-Alert Tones.md"
    ];
    private readonly IReadOnlyList<DocumentationPage> pages;

    public DocumentationCatalog(
        string documentationRoot,
        IEnumerable<string>? pagePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentationRoot);
        string rootPath = Path.GetFullPath(documentationRoot);

        pages = (pagePaths ?? DefaultPagePaths)
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DocumentationPage(
                FormatTitle(Path.GetFileName(path)),
                path,
                Path.Combine(rootPath, path.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(page => page.RelativePath, DocumentationPathComparer.Instance)
            .ToArray();
    }

    public static DocumentationCatalog OpenDefault()
        => new(Path.Combine(AppContext.BaseDirectory, "Documentation"));

    public async Task<IReadOnlyList<DocumentationPage>> FindAsync(
        string? searchText = null,
        CancellationToken cancellationToken = default)
    {
        string query = searchText?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return pages;

        Task<DocumentationPage?>[] searches = pages.Select(async page =>
        {
            if (page.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                return page;
            string markdown = await ReadAsync(page, cancellationToken).ConfigureAwait(false);
            return markdown.Contains(query, StringComparison.OrdinalIgnoreCase) ? page : null;
        }).ToArray();
        DocumentationPage?[] matches = await Task.WhenAll(searches).ConfigureAwait(false);
        return matches.Where(page => page is not null).Cast<DocumentationPage>().ToArray();
    }

    public async Task<string> ReadAsync(
        DocumentationPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        DocumentationPage? knownPage = pages.FirstOrDefault(candidate =>
            candidate.RelativePath.Equals(page.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (knownPage is null)
            throw new InvalidOperationException("The documentation page is outside the configured documentation set.");

        return await File.ReadAllTextAsync(
            knownPage.FilePath,
            cancellationToken).ConfigureAwait(false);
    }

    public static string FormatTitle(string value)
    {
        string name = Path.GetFileNameWithoutExtension(value ?? string.Empty);
        return SortPrefixRegex.Replace(name, string.Empty).Trim();
    }

    private static string NormalizeRelativePath(string value)
    {
        string path = (value ?? string.Empty).Replace('\\', '/').Trim('/');
        if (path.Length == 0 || path.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("Documentation paths must remain under the configured documentation root.", nameof(value));
        return path;
    }

    private sealed class DocumentationPathComparer : IComparer<string>
    {
        public static DocumentationPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            string[] leftParts = (left ?? string.Empty).Split('/');
            string[] rightParts = (right ?? string.Empty).Split('/');
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
