// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Mobile;

/// <summary>Stages a YAML-only export for validation before opening its destination.</summary>
internal sealed class BufferedYamlExport : IExportDocumentSet, IWritableDocument, IDisposable
{
    private MemoryStream contents = new();
    public string DisplayName => "configuration.yaml";
    public string? OriginIdentity => null;
    public IWritableDocument Primary => this;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new MemoryStream(contents.ToArray(), writable: false));
    }

    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        contents.Dispose();
        contents = new MemoryStream();
        return ValueTask.FromResult<Stream>(contents);
    }

    public ValueTask<IWritableDocument> CreateCompanionAsync(string safeRelativeName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This destination supports YAML-only exports.");

    public ValueTask<IReadableDocument?> ResolveExportedCompanionAsync(string safeRelativeName, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadableDocument?>(null);

    public void Dispose() => contents.Dispose();
}
