// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;
using System.Text;

namespace DvmConsole.Core.IO;

public static class PortableFileName
{
    public static string Segment(string value, int maximumLength = 64)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 16);
        string name = new(value.Select(character => character < ' ' ||
            "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray());
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
            name = "unknown";
        string stem = name.Split('.', 2)[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'))
            name = "_" + name;
        if (name.Length > maximumLength || !StringComparer.Ordinal.Equals(name, value))
        {
            string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10];
            int prefixLength = Math.Min(name.Length, maximumLength - 11);
            if (prefixLength > 0 && char.IsHighSurrogate(name[prefixLength - 1]))
                prefixLength--;
            name = name[..prefixLength].TrimEnd('.', ' ') + "-" + suffix;
        }
        return name;
    }
}
