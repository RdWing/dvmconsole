// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

internal sealed record ConfigurationStudioSaveState(
    string Yaml,
    string? KeyFileIdentifier,
    string? KeyFileHash,
    bool KeyFileDirty,
    string KeyFileContent,
    IReadOnlyDictionary<string, string> AliasContents,
    IReadOnlyDictionary<string, string> AliasFileHashes,
    IReadOnlyDictionary<string, string> AliasFileBaselines,
    IReadOnlyList<ConfigurationValidationIssue> Issues);
