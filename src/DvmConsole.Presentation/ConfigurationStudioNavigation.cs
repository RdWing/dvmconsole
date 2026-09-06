// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Presentation;

public enum ConfigurationStudioSection
{
    Overview,
    Systems,
    Zones,
    Streams,
    Groups,
    EncryptionKeys,
    Files
}

public sealed record ConfigurationStudioNavigationItem(
    ConfigurationStudioSection Section,
    string Label,
    string Description);
