// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;

namespace DvmConsole.Desktop;

internal static class WebStreamSelectionIdentity
{
    public static string Create(string? codeplugPath, WebStreamViewModel stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ConfigurationWebStreamAuthorizationIdentity.Create(codeplugPath,
            stream.Name, stream.Url, stream.AuthUsername, stream.AuthPassword);
    }

    public static string Create(string? codeplugPath, WebStreamConfiguration stream)
        => ConfigurationWebStreamAuthorizationIdentity.Create(codeplugPath, stream);

    public static bool IsAuthorized(IEnumerable<string> persistedIdentities,
        string? codeplugPath, WebStreamViewModel stream)
    {
        string identity = Create(codeplugPath, stream);
        return identity.Length > 0 && persistedIdentities.Contains(identity, StringComparer.Ordinal);
    }

    public static bool IsVersioned(string value)
        => ConfigurationWebStreamAuthorizationIdentity.IsVersioned(value);
}
