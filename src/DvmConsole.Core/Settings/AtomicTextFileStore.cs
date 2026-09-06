// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Settings;

using DvmConsole.Core.IO;

/// <summary>
/// Replaces one UTF-8 text file atomically without exposing a partially written
/// destination to readers.
/// </summary>
public sealed class AtomicTextFileStore
{
    public AtomicTextFileStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }
    public bool Exists => File.Exists(Path);

    public string ReadAllText()
        => File.ReadAllText(Path);

    public string ReadAllText(int maximumBytes, string resourceName)
        => BoundedResourceReader.ReadUtf8File(Path, maximumBytes, resourceName);

    public void WriteAllText(string content, bool preserveBackup = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
            AppDataFileProtection.EnsureDirectory(directory);

        string temporaryPath = $"{Path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            AppDataFileProtection.EnsureFile(temporaryPath);
            if (!preserveBackup && File.Exists(Path))
            {
                File.Copy(Path, Path + ".backup", overwrite: true);
                AppDataFileProtection.EnsureFile(Path + ".backup");
            }
            // The protected temporary file retains its permissions after the
            // atomic rename. There must be no fallible work after commit.
            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public void CopyTo(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string destination = System.IO.Path.GetFullPath(destinationPath);
        string? directory = System.IO.Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.Copy(Path, destination, overwrite: true);
    }

    public void Delete()
    {
        if (Exists)
            File.Delete(Path);
    }
}
