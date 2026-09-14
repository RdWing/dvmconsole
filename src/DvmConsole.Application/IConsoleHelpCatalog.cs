// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed record ConsoleHelpTopic(string Id, string Title, string Section);

/// <summary>Read-only help content; hosts own package locations and external navigation.</summary>
public interface IConsoleHelpCatalog
{
    Task<IReadOnlyList<ConsoleHelpTopic>> FindAsync(string? searchText = null, CancellationToken cancellationToken = default);
    Task<string> ReadAsync(string topicId, CancellationToken cancellationToken = default);
    ConsoleHelpTopic? ResolveLink(string topicId, string link);
}
