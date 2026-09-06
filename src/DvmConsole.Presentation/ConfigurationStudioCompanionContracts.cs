// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

public sealed record ConfigurationStudioKeyCompanion(
    string Identifier,
    string? Content,
    string? ContentHash,
    string? LoadIssue,
    bool LoadIssueIsWarning);

public sealed record ConfigurationStudioAliasCompanion(
    string Identifier,
    IReadOnlyList<string> References,
    string Content,
    string? ContentHash);

public sealed record ConfigurationStudioCompanionSnapshot(
    ConfigurationStudioKeyCompanion? KeyFile,
    IReadOnlyList<ConfigurationStudioAliasCompanion> AliasFiles,
    IReadOnlyList<string> AliasErrors,
    IReadOnlyList<string> AliasWarnings);

public interface IConfigurationStudioCompanionSource
{
    ConfigurationStudioCompanionSnapshot Load(ConfigurationDocument document);
    ConfigurationDocument ParseDraft(string yaml, ConfigurationDocument currentDocument);
    ConfigurationDocument AcceptSaved(
        ConfigurationDocument currentDocument,
        string hostDocumentIdentity,
        string yaml);
    string CreateWebStreamAuthorizationIdentity(
        string hostDocumentIdentity,
        WebStreamConfiguration stream);
}
