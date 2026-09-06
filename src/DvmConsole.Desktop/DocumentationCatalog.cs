// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.RegularExpressions;

namespace DvmConsole.Desktop;

internal sealed record DocumentationPage(
    string Title,
    string RelativePath,
    string FilePath);

internal sealed partial class DocumentationCatalog
{
    private readonly string documentationRoot;
    private readonly HashSet<string> assetPaths;
    private readonly IReadOnlyList<DocumentationPage> pages;

    public DocumentationCatalog(
        string documentationRoot,
        IEnumerable<string> pagePaths,
        IEnumerable<string>? assets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentationRoot);
        this.documentationRoot = Path.GetFullPath(documentationRoot);
        assetPaths = (assets ?? [])
            .Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        pages = pagePaths
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DocumentationPage(
                FormatTitle(Path.GetFileName(path)),
                path,
                ResolveUnderRoot(path)))
            .OrderBy(page => page.RelativePath, DocumentationPathComparer.Instance)
            .ToArray();
    }

    public static DocumentationCatalog OpenDefault()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Documentation");
        string manifestPath = Path.Combine(root, "manifest.json");
        DocumentationManifest manifest = JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath),
            DesktopSettingsJsonContext.Default.DocumentationManifest)
            ?? throw new InvalidDataException("The documentation manifest is empty.");
        return new DocumentationCatalog(root, manifest.Pages, manifest.Assets);
    }

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

        string markdown = await File.ReadAllTextAsync(
            knownPage.FilePath,
            cancellationToken).ConfigureAwait(false);
        return LocalImageRegex().Replace(markdown, match => ResolveImageLink(knownPage, match));
    }

    public static string FormatTitle(string value)
    {
        string name = Path.GetFileNameWithoutExtension(value ?? string.Empty);
        return SortPrefixRegex().Replace(name, string.Empty).Trim();
    }

    private static string NormalizeRelativePath(string value)
    {
        string path = (value ?? string.Empty).Replace('\\', '/').Trim('/');
        if (path.Length == 0 || path.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("Documentation paths must remain under the configured documentation root.", nameof(value));
        return path;
    }

    private string ResolveUnderRoot(string relativePath)
    {
        string fullPath = Path.GetFullPath(
            relativePath.Replace('/', Path.DirectorySeparatorChar),
            documentationRoot);
        string relative = Path.GetRelativePath(documentationRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Documentation paths must remain under the configured documentation root.",
                nameof(relativePath));
        }
        return fullPath;
    }

    private string ResolveImageLink(DocumentationPage page, Match match)
    {
        string value = match.Groups[1].Value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absoluteUri))
        {
            if (absoluteUri.Scheme is "http" or "https")
                return match.Value;
            throw new InvalidDataException("Documentation images may use only packaged assets or HTTP(S) URLs.");
        }

        string pageDirectory = Path.GetDirectoryName(page.RelativePath) ?? string.Empty;
        if (Path.IsPathRooted(value))
            throw new InvalidDataException("Documentation image paths must be relative.");
        string imagePath = Path.GetFullPath(Path.Combine(documentationRoot, pageDirectory, value));
        string normalized = Path.GetRelativePath(documentationRoot, imagePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidDataException("Documentation image paths must remain under the packaged asset root.");
        if (!assetPaths.Contains(normalized))
            throw new InvalidDataException($"Documentation image is not declared in the manifest: {value}");
        string replacement = new Uri(imagePath).AbsoluteUri;
        int valueOffset = match.Groups[1].Index - match.Index;
        return match.Value[..valueOffset] +
               replacement +
               match.Value[(valueOffset + match.Groups[1].Length)..];
    }

    [GeneratedRegex(@"^\d+\s*-\s*")]
    private static partial Regex SortPrefixRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\(([^)]+)\)")]
    private static partial Regex LocalImageRegex();

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

internal sealed record DocumentationManifest(string[] Pages, string[] Assets);
