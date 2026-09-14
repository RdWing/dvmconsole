// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Storage;

/// <summary>Resolves data beside ordinary apphosts or inside a macOS bundle.</summary>
public static class BundledResources
{
    public static string Root(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var directory = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        if (directory.Name == "MacOS" && directory.Parent?.Name == "Contents")
            return Path.Combine(directory.Parent.FullName, "Resources");
        return directory.FullName;
    }
}
