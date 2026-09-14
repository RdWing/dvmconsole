// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Text;

namespace DvmConsole.Storage;

/// <summary>Preserves the existing v2 identity for operator-authorized automatic stream access.</summary>
public static class ConfigurationWebStreamAuthorizationIdentity
{
    private const string VersionPrefix = "v2:";

    public static string Create(string? documentIdentity, WebStreamConfiguration stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Create(documentIdentity, stream.Name?.Trim() ?? string.Empty, stream.Url,
            stream.AuthUsername?.Trim() ?? string.Empty, stream.AuthPassword ?? string.Empty);
    }

    public static string Create(string? documentIdentity, string? name, string? url,
        string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(documentIdentity) ||
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return string.Empty;
        string canonicalUrl = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        string material = string.Join('\n', Path.GetFullPath(documentIdentity), name, canonicalUrl, username, password);
        return VersionPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public static bool IsVersioned(string value)
        => value?.StartsWith(VersionPrefix, StringComparison.Ordinal) == true;
}
