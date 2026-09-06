// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers;
using System.Security.Cryptography;

namespace DvmConsole.Configuration.Yaml;

internal static class PortableCompanionNameAllocator
{
    private static readonly SearchValues<char> InvalidCharacters = SearchValues.Create(
        "<>:\"/\\|?*");

    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string Allocate(
        string reference,
        ReadOnlySpan<byte> content,
        IReadOnlyDictionary<string, byte[]> existing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(existing);

        string normalized = reference.Replace('\\', '/');
        string candidate = Sanitize(normalized[(normalized.LastIndexOf('/') + 1)..]);
        if (!existing.TryGetValue(candidate, out byte[]? current))
            return candidate;
        if (content.SequenceEqual(current))
            return candidate;

        string extension = Path.GetExtension(candidate);
        string stem = Path.GetFileNameWithoutExtension(candidate);
        string suffix = Convert.ToHexString(SHA256.HashData(content))[..8].ToLowerInvariant();
        string suffixed = $"{stem}-{suffix}{extension}";
        if (!existing.TryGetValue(suffixed, out current) || content.SequenceEqual(current))
            return suffixed;

        for (int discriminator = 2; ; discriminator++)
        {
            string unique = $"{stem}-{suffix}-{discriminator}{extension}";
            if (!existing.TryGetValue(unique, out current) || content.SequenceEqual(current))
                return unique;
        }
    }

    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<char> buffer = value.Length <= 256
            ? stackalloc char[value.Length]
            : new char[value.Length];
        int length = 0;
        foreach (char character in value)
        {
            if (character < ' ' || InvalidCharacters.Contains(character))
                continue;
            buffer[length++] = character;
        }

        while (length > 0 && buffer[length - 1] is ' ' or '.')
            length--;

        string candidate = length == 0
            ? "companion"
            : new string(buffer[..length]);
        string deviceStem = candidate.Split('.', 2)[0];
        return ReservedWindowsNames.Contains(deviceStem)
            ? "_" + candidate
            : candidate;
    }
}
