// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

internal static class FileSystemPathIdentity
{
    public static IEqualityComparer<string> Comparer
        => Core.Configuration.FileSystemPathIdentity.Comparer;

    public static bool AreEquivalent(string first, string second)
    {
        return Core.Configuration.FileSystemPathIdentity.AreEquivalent(first, second);
    }

    public static bool IsUnderRoot(string rootPath, string path)
    {
        return Core.Configuration.FileSystemPathIdentity.IsUnderRoot(rootPath, path);
    }
}
