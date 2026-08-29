using System.Security.Cryptography;
using System.Text;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Desktop;

// Binds automatic network access to the exact codeplug and stream definition
// that the operator previously started. Display names remain presentation-only.
internal static class WebStreamSelectionIdentity
{
    private const string VersionPrefix = "v2:";

    public static string Create(string? codeplugPath, WebStreamViewModel stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (string.IsNullOrWhiteSpace(codeplugPath) ||
            !Uri.TryCreate(stream.Url, UriKind.Absolute, out Uri? uri))
        {
            return string.Empty;
        }

        string canonicalUrl = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        string material = string.Join('\n',
            Path.GetFullPath(codeplugPath),
            stream.Name,
            canonicalUrl,
            stream.AuthUsername,
            stream.AuthPassword);
        return VersionPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public static string Create(string? codeplugPath, WebStreamConfiguration stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (string.IsNullOrWhiteSpace(codeplugPath) ||
            !Uri.TryCreate(stream.Url, UriKind.Absolute, out Uri? uri))
        {
            return string.Empty;
        }

        string canonicalUrl = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        string material = string.Join('\n',
            Path.GetFullPath(codeplugPath),
            stream.Name?.Trim() ?? string.Empty,
            canonicalUrl,
            stream.AuthUsername?.Trim() ?? string.Empty,
            stream.AuthPassword ?? string.Empty);
        return VersionPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public static bool IsAuthorized(
        IEnumerable<string> persistedIdentities,
        string? codeplugPath,
        WebStreamViewModel stream)
    {
        string identity = Create(codeplugPath, stream);
        return identity.Length > 0 && persistedIdentities.Contains(identity, StringComparer.Ordinal);
    }

    public static bool IsVersioned(string value)
        => value?.StartsWith(VersionPrefix, StringComparison.Ordinal) == true;
}
