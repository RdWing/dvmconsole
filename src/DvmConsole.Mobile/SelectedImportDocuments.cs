// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Platform.Storage;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

/// <summary>Only explicitly selected files are eligible as import companions.</summary>
public sealed class SelectedImportDocuments : IImportDocumentSet
{
    private readonly IReadOnlyList<IReadableDocument> companions;

    public SelectedImportDocuments(IReadableDocument primary, IReadOnlyList<IReadableDocument> companions)
    {
        Primary = primary ?? throw new ArgumentNullException(nameof(primary));
        ArgumentNullException.ThrowIfNull(companions);
        this.companions = companions.ToArray();
    }

    public IReadableDocument Primary { get; }

    public ValueTask<IReadableDocument?> ResolveCompanionAsync(
        string relativeReference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = relativeReference.Replace('\\', '/').Split('/')[^1];
        IReadableDocument[] matches = companions.Where(document =>
            string.Equals(document.DisplayName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException($"More than one selected file is named '{name}'. Select an unambiguous companion set.");
        return ValueTask.FromResult(matches.SingleOrDefault());
    }
}

internal sealed class StorageFileDocument(IStorageFile file) : IReadableDocument
{
    public string DisplayName => file.Name;
    public string? OriginIdentity => file.Path.AbsoluteUri;

    public async ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await file.OpenReadAsync().ConfigureAwait(false);
    }
}
