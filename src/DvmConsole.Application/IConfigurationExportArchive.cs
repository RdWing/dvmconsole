// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Private staging for a validated, self-contained configuration export.</summary>
public interface IConfigurationExportArchive : IExportDocumentSet, IAsyncDisposable
{
    ValueTask<IReadableDocument> CreateArchiveAsync(CancellationToken cancellationToken = default);
}
