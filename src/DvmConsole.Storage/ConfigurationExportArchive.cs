// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.IO.Compression;
using DvmConsole.Core.Settings;

namespace DvmConsole.Storage;

/// <summary>Stages export files and ZIP output under a host-owned private directory.</summary>
public sealed class ConfigurationExportArchive : IConfigurationExportArchive
{
    private readonly string root;
    private readonly Dictionary<string, FileConfigurationDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private bool sealedArchive;
    private bool disposed;

    public ConfigurationExportArchive(string stagingParent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingParent);
        root = Path.Combine(Path.GetFullPath(stagingParent), Guid.NewGuid().ToString("N"));
        AppDataFileProtection.EnsureDirectory(root);
        Primary = AddDocument("configuration.yaml");
    }

    public IWritableDocument Primary { get; }

    public ValueTask<IWritableDocument> CreateCompanionAsync(string safeRelativeName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sealedArchive) throw new InvalidOperationException("The export archive is already sealed.");
        ValidateName(safeRelativeName);
        if (documents.ContainsKey(safeRelativeName))
            throw new InvalidDataException("An export companion conflicts with an existing document.");
        return ValueTask.FromResult<IWritableDocument>(AddDocument(safeRelativeName));
    }

    public ValueTask<IReadableDocument?> ResolveExportedCompanionAsync(string safeRelativeName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateName(safeRelativeName);
        return ValueTask.FromResult<IReadableDocument?>(documents.GetValueOrDefault(safeRelativeName));
    }

    public async ValueTask<IReadableDocument> CreateArchiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sealedArchive) throw new InvalidOperationException("The export archive is already sealed.");
        sealedArchive = true;
        var archiveDocument = new FileConfigurationDocument(Path.Combine(root, "configuration.zip"), protectAsAppData: true);
        await using (Stream output = await archiveDocument.OpenWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach ((string name, FileConfigurationDocument document) in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                await using Stream source = await document.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                await using Stream destination = entry.Open();
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }
        return archiveDocument;
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        Directory.Delete(root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private FileConfigurationDocument AddDocument(string name)
    {
        var document = new FileConfigurationDocument(Path.Combine(root, "documents", name), protectAsAppData: true);
        documents.Add(name, document);
        return document;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            throw new InvalidDataException("An export companion must have a plain filename.");
    }
}
