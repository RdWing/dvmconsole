// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

namespace DvmConsole.Core.Configuration;

/// <summary>
/// Compares paths using the identity and case behavior of their filesystem.
/// Existing files are compared by volume and file identifier, so hard links
/// and symbolic-link aliases cannot bypass same-file checks.
/// </summary>
public static partial class FileSystemPathIdentity
{
    private static readonly ConcurrentDictionary<string, bool> CaseSensitivityByDirectory =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static IEqualityComparer<string> Comparer { get; } = new PhysicalPathComparer();

    public static bool Equals(string? left, string? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.Equals(left, right, StringComparison.Ordinal);
        return AreEquivalent(left, right);
    }

    public static bool AreEquivalent(string first, string second)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(first);
        ArgumentException.ThrowIfNullOrWhiteSpace(second);

        string resolvedFirst = ResolvePath(first);
        string resolvedSecond = ResolvePath(second);
        if (TryGetPhysicalIdentity(resolvedFirst, out PhysicalIdentity firstIdentity) &&
            TryGetPhysicalIdentity(resolvedSecond, out PhysicalIdentity secondIdentity))
        {
            return firstIdentity == secondIdentity;
        }

        bool caseSensitive = IsCaseSensitiveForPath(resolvedFirst) ||
            IsCaseSensitiveForPath(resolvedSecond);
        return string.Equals(
            resolvedFirst,
            resolvedSecond,
            caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnderRoot(string rootPath, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string resolvedRoot = ResolvePath(rootPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string resolvedPath = ResolvePath(path);
        if (AreEquivalent(resolvedRoot, resolvedPath))
            return true;

        string prefix = resolvedRoot + Path.DirectorySeparatorChar;
        bool caseSensitive = IsCaseSensitiveForPath(resolvedRoot);
        return resolvedPath.StartsWith(
            prefix,
            caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePath(string path)
    {
        return ResolvePath(Path.GetFullPath(path), linkDepth: 0);
    }

    private static string ResolvePath(string fullPath, int linkDepth)
    {
        if (linkDepth > 40)
            throw new InvalidDataException($"Path '{fullPath}' contains a cyclic link.");

        string root = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException(
            $"Path '{fullPath}' does not have a filesystem root.");
        string relative = fullPath[root.Length..];
        if (relative.Length == 0)
            return root;

        string current = root;
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length; index++)
        {
            string candidate = Path.Combine(current, segments[index]);
            try
            {
                if (!TryGetLinkTarget(candidate, out string? linkTarget))
                {
                    current = candidate;
                    continue;
                }

                string resolvedLinkTarget = linkTarget!;
                string targetPath = Path.IsPathRooted(resolvedLinkTarget)
                    ? Path.GetFullPath(resolvedLinkTarget)
                    : Path.GetFullPath(resolvedLinkTarget, Path.GetDirectoryName(candidate)!);
                if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
                    throw new InvalidDataException($"Path '{fullPath}' contains a broken or cyclic link.");

                if (index == segments.Length - 1)
                    return ResolvePath(targetPath, linkDepth + 1);
                if (File.Exists(targetPath))
                    throw new InvalidDataException($"Path '{fullPath}' traverses through a file link.");

                string combined = targetPath;
                for (int remaining = index + 1; remaining < segments.Length; remaining++)
                    combined = Path.Combine(combined, segments[remaining]);
                return ResolvePath(Path.GetFullPath(combined), linkDepth + 1);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                exception is not InvalidDataException)
            {
                throw new InvalidDataException($"Path '{fullPath}' contains an unreadable link.", exception);
            }
        }

        return Path.GetFullPath(current);
    }

    private static bool TryGetLinkTarget(string path, out string? target)
    {
        if (!OperatingSystem.IsWindows())
            return TryReadUnixLink(path, out target);

        FileSystemInfo information = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        information.Refresh();
        target = information.LinkTarget;
        if (target is not null)
            return true;

        if (File.Exists(path) || Directory.Exists(path))
            return false;

        var directory = new DirectoryInfo(path);
        directory.Refresh();
        target = directory.LinkTarget;
        return target is not null;
    }

    private static bool TryReadUnixLink(string path, out string? target)
    {
        byte[] buffer = new byte[256];
        while (buffer.Length <= 32 * 1024)
        {
            nint length = ReadLink(path, buffer, (nuint)buffer.Length);
            if (length < 0)
            {
                target = null;
                return false;
            }
            if (length < buffer.Length)
            {
                target = Encoding.UTF8.GetString(buffer, 0, checked((int)length));
                return true;
            }
            buffer = new byte[buffer.Length * 2];
        }

        throw new InvalidDataException($"Path '{path}' has an excessively long link target.");
    }

    private static bool IsCaseSensitiveForPath(string path)
    {
        string directory = Directory.Exists(path)
            ? path
            : Path.GetDirectoryName(path) ?? Path.GetPathRoot(path) ?? path;
        while (!Directory.Exists(directory))
        {
            string? parent = Path.GetDirectoryName(directory);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, directory, StringComparison.Ordinal))
                return DefaultCaseSensitivity;
            directory = parent;
        }

        string key = ResolvePath(directory);
        return CaseSensitivityByDirectory.GetOrAdd(key, ProbeCaseSensitivity);
    }

    private static bool ProbeCaseSensitivity(string directory)
    {
        if (OperatingSystem.IsWindows())
            return false;

        string token = Guid.NewGuid().ToString("N");
        string mixedName = $".dVmCoNsOlE-case-{token}";
        string alternateName = mixedName.ToUpperInvariant();
        string probePath = Path.Combine(directory, mixedName);
        string alternatePath = Path.Combine(directory, alternateName);
        try
        {
            using (new FileStream(
                       probePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete,
                       bufferSize: 1,
                       FileOptions.DeleteOnClose))
            {
                return !File.Exists(alternatePath);
            }
        }
        catch (IOException)
        {
            return DefaultCaseSensitivity;
        }
        catch (UnauthorizedAccessException)
        {
            return DefaultCaseSensitivity;
        }
    }

    private static bool DefaultCaseSensitivity => !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    private static bool TryGetPhysicalIdentity(string path, out PhysicalIdentity identity)
    {
        identity = default;
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;

        if (OperatingSystem.IsWindows())
            return TryGetWindowsIdentity(path, out identity);
        if (OperatingSystem.IsLinux())
            return TryGetLinuxIdentity(path, out identity);
        if (OperatingSystem.IsMacOS())
            return TryGetMacIdentity(path, out identity);
        return false;
    }

    private static bool TryGetWindowsIdentity(string path, out PhysicalIdentity identity)
    {
        identity = default;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
                return false;
            ulong fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            identity = new PhysicalIdentity(information.VolumeSerialNumber, fileId);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetLinuxIdentity(string path, out PhysicalIdentity identity)
    {
        identity = default;
        try
        {
            if (Statx(AtFdcwd, path, 0, StatxIno, out LinuxStatx stat) != 0)
                return false;
            ulong device = ((ulong)stat.DeviceMajor << 32) | stat.DeviceMinor;
            identity = new PhysicalIdentity(device, stat.Inode);
            return true;
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetMacIdentity(string path, out PhysicalIdentity identity)
    {
        identity = default;
        try
        {
            // Intel macOS exports the legacy 32-bit-inode layout as "stat".
            // Match the 64-bit layout below to the SDK's architecture-specific ABI.
            int result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? MacStatInode64(path, out MacStatBuffer stat)
                : MacStat(path, out stat);
            if (result != 0)
                return false;
            identity = new PhysicalIdentity(unchecked((uint)stat.Device), stat.Inode);
            return true;
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    private readonly record struct PhysicalIdentity(ulong Volume, ulong File);

    private sealed class PhysicalPathComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
            => FileSystemPathIdentity.Equals(x, y);

        public int GetHashCode(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            string resolved = ResolvePath(value);
            if (TryGetPhysicalIdentity(resolved, out PhysicalIdentity identity))
                return identity.GetHashCode();
            return (IsCaseSensitiveForPath(resolved)
                    ? StringComparer.Ordinal
                    : StringComparer.OrdinalIgnoreCase)
                .GetHashCode(resolved);
        }
    }

    private const int AtFdcwd = -100;
    private const uint StatxIno = 0x00000100;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [LibraryImport(
        "libc",
        EntryPoint = "readlink",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ReadLink(
        string path,
        [Out, MarshalUsing(CountElementName = nameof(bufferSize))]
        byte[] buffer,
        nuint bufferSize);

    [LibraryImport(
        "libc",
        EntryPoint = "statx",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx stat);

    [LibraryImport(
        "libSystem.B.dylib",
        EntryPoint = "stat",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacStat(
        string path,
        out MacStatBuffer stat);

    [LibraryImport(
        "libSystem.B.dylib",
        EntryPoint = "stat$INODE64",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacStatInode64(
        string path,
        out MacStatBuffer stat);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct LinuxStatx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        private StatxTimestamp AccessTime;
        private StatxTimestamp BirthTime;
        private StatxTimestamp ChangeTime;
        private StatxTimestamp ModificationTime;
        public uint RdevMajor;
        public uint RdevMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Seconds;
        public uint Nanoseconds;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacStatBuffer
    {
        public int Device;
        public ushort Mode;
        public ushort LinkCount;
        public ulong Inode;
        public uint UserId;
        public uint GroupId;
        public int Rdev;
        private int Padding;
        private MacTimespec AccessTime;
        private MacTimespec ModificationTime;
        private MacTimespec ChangeTime;
        private MacTimespec BirthTime;
        public long Size;
        public long Blocks;
        public int BlockSize;
        public uint Flags;
        public uint Generation;
        private int Spare;
        private long Spare0;
        private long Spare1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MacTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }
}
