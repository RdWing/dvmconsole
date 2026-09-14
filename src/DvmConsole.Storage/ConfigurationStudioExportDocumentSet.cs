// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;

namespace DvmConsole.Storage;

// Gives a dirty Studio export a self-contained view of the current in-memory
// YAML and companion bytes. An imported document's original path may no longer
// exist, and the managed Studio draft must never depend on that path.
public sealed class ConfigurationStudioExportDocumentSet(
    string yaml,
    string displayName,
    IReadOnlyDictionary<string, string> companionContents) : IImportDocumentSet
{
    private readonly IReadOnlyDictionary<string, string> companionContents =
        companionContents ?? throw new ArgumentNullException(nameof(companionContents));

    public IReadableDocument Primary { get; } = new InMemoryConfigurationDocument(
        string.IsNullOrWhiteSpace(displayName) ? "codeplug.yml" : displayName,
        "primary",
        Encoding.UTF8.GetBytes(yaml ?? throw new ArgumentNullException(nameof(yaml))));

    public ValueTask<IReadableDocument?> ResolveCompanionAsync(
        string relativeReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return companionContents.TryGetValue(relativeReference, out string? content)
            ? ValueTask.FromResult<IReadableDocument?>(new InMemoryConfigurationDocument(
                Path.GetFileName(relativeReference),
                relativeReference,
                Encoding.UTF8.GetBytes(content)))
            : ValueTask.FromResult<IReadableDocument?>(null);
    }

    private sealed class InMemoryConfigurationDocument(
        string displayName,
        string originIdentity,
        byte[] content) : IReadableDocument
    {
        public string DisplayName { get; } = displayName;
        public string OriginIdentity { get; } = "studio-draft:" + originIdentity;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }
}
