// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;

namespace DvmConsole.Application;

/// <summary>Editor state required to prepare a reviewed save; file access belongs to the host.</summary>
public interface IConfigurationStudioSaveSource
{
    ConfigurationDocument Document { get; }
    ConsoleConfiguration Configuration { get; }
    string DocumentIdentity { get; }
    ConfigurationStudioSaveState CaptureSaveState();
    void ApplyOperatorStateForSave(UserSettings settings, string destinationIdentity, bool identityChanged);
    string BuildIdentityMigrationReviewText();
}
