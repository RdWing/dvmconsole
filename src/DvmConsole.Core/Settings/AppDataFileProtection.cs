// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Settings;

using System.Security.AccessControl;
using System.Security.Principal;

/// <summary>
/// Applies the private owner-only policy used by DVM Console-managed data.
/// New recording files receive owner-only Windows ACLs or explicit Unix permissions.
/// Legacy directory helpers leave Windows directory inheritance unchanged.
/// </summary>
public static class AppDataFileProtection
{
    /// <summary>Repairs only an identified app-owned file, never its selected parent directory.</summary>
    public static void EnsurePrivateOwnedFile(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
            throw new InvalidDataException("An app-owned recording file cannot be a symbolic link.");
        if (!file.Exists)
            return;
        if (!OperatingSystem.IsWindows())
        {
            EnsureFile(path);
            return;
        }
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User ?? throw new IOException("The current file owner could not be resolved.");
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    /// <summary>Creates a new app-owned file without inheriting a public custom-root policy.</summary>
    public static FileStream CreatePrivateFile(
        string path,
        FileAccess access = FileAccess.ReadWrite,
        FileShare share = FileShare.None,
        int bufferSize = 4096,
        FileOptions options = FileOptions.None)
    {
        if (OperatingSystem.IsWindows())
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier owner = identity.User ?? throw new IOException("The current file owner could not be resolved.");
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            FileSystemRights rights = access switch
            {
                FileAccess.Read => FileSystemRights.Read,
                FileAccess.Write => FileSystemRights.Write,
                _ => FileSystemRights.Read | FileSystemRights.Write
            };
            return new FileInfo(path).Create(FileMode.CreateNew, rights, share, bufferSize, options, security);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = access,
            Share = share,
            BufferSize = bufferSize,
            Options = options,
            UnixCreateMode = PrivateFileMode
        });
    }

    public const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureDirectory(string path, bool repairExistingTree = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
            return;

        SetDirectoryMode(path);
        if (!repairExistingTree)
            return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (FileSystemInfo entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            if (entry.LinkTarget is not null)
                continue;
            if (entry is DirectoryInfo)
                SetDirectoryMode(entry.FullName);
            else if (entry is FileInfo)
                SetFileMode(entry.FullName);
        }
    }

    public static void EnsureFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows() && File.Exists(path))
            SetFileMode(path);
    }

    private static void SetDirectoryMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void SetFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
