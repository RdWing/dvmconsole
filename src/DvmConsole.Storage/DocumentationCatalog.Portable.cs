// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;

namespace DvmConsole.Storage;

public sealed partial class DocumentationCatalog
{
    async Task<IReadOnlyList<ConsoleHelpTopic>> IConsoleHelpCatalog.FindAsync(string? searchText, CancellationToken cancellationToken)
        => (await FindAsync(searchText, cancellationToken).ConfigureAwait(false)).Select(ToTopic).ToArray();

    Task<string> IConsoleHelpCatalog.ReadAsync(string topicId, CancellationToken cancellationToken)
        => ReadAsync(pages.FirstOrDefault(page => page.RelativePath == topicId)
            ?? throw new InvalidOperationException("The help topic is not in the bundled guide."), cancellationToken);

    ConsoleHelpTopic? IConsoleHelpCatalog.ResolveLink(string topicId, string link)
    {
        DocumentationPage? current = pages.FirstOrDefault(page => page.RelativePath == topicId);
        if (current is null || Uri.TryCreate(link, UriKind.Absolute, out _)) return null;
        // A synthetic URI resolves relative links without treating them as host paths.
        var root = new Uri("https://bundled-help.invalid/");
        var source = new Uri(root, current.RelativePath);
        if (!Uri.TryCreate(source, link, out var resolved) || resolved.Host != root.Host) return null;
        string path = Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
        var destination = pages.FirstOrDefault(page => page.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        return destination is null ? null : ToTopic(destination);
    }

    private static ConsoleHelpTopic ToTopic(DocumentationPage page)
        => new(page.RelativePath, page.Title, string.Join(" › ",
            page.RelativePath.Split('/').SkipLast(1).Select(FormatTitle)));
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DocumentationManifest))]
internal sealed partial class DocumentationJsonContext : JsonSerializerContext;
