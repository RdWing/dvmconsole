// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;

namespace DvmConsole.Storage;

// Filesystem paths are confined to Storage and supplied by the host.
// The configuration library works with document handles and streams.
public sealed class FileConfigurationDocumentSet :
    IImportDocumentSet,
    ITransactionalExportDocumentSet
{
    private readonly string directory;

    public FileConfigurationDocumentSet(string primaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        string fullPath = Path.GetFullPath(primaryPath);
        directory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        PrimaryDocument = new FileConfigurationDocument(fullPath);
    }

    public FileConfigurationDocument PrimaryDocument { get; }
    public IReadableDocument Primary => PrimaryDocument;
    IWritableDocument IExportDocumentSet.Primary => PrimaryDocument;

    public ValueTask<IReadableDocument?> ResolveCompanionAsync(
        string relativeReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(relativeReference))
            return ValueTask.FromResult<IReadableDocument?>(null);
        string path = Path.IsPathRooted(relativeReference)
            ? Path.GetFullPath(relativeReference)
            : Path.GetFullPath(Path.Combine(directory, relativeReference));
        return ValueTask.FromResult<IReadableDocument?>(
            File.Exists(path) ? new FileConfigurationDocument(path) : null);
    }

    public ValueTask<IWritableDocument> CreateCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ResolveSafeCompanionPath(safeRelativeName);
        return ValueTask.FromResult<IWritableDocument>(new FileConfigurationDocument(path));
    }

    public ValueTask<IReadableDocument?> ResolveExportedCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ResolveSafeCompanionPath(safeRelativeName);
        return ValueTask.FromResult<IReadableDocument?>(
            File.Exists(path) ? new FileConfigurationDocument(path) : null);
    }

    public ValueTask<IExportDocumentTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IExportDocumentTransaction>(
            new FileConfigurationExportTransaction(PrimaryDocument.Path));
    }

    private string ResolveSafeCompanionPath(string name)
    {
        string fileName = Path.GetFileName(name);
        if (fileName.Length == 0 || !string.Equals(fileName, name, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe companion name '{name}'.");
        return Path.Combine(directory, fileName);
    }
}

internal sealed class FileConfigurationExportTransaction : IExportDocumentTransaction
{
    private readonly string destinationDirectory;
    private readonly string stagingDirectory;
    private readonly Dictionary<string, string> destinations = new(StringComparer.OrdinalIgnoreCase);
    private int completed;

    public FileConfigurationExportTransaction(string primaryPath)
    {
        string fullPath = Path.GetFullPath(primaryPath);
        destinationDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        stagingDirectory = Path.Combine(
            destinationDirectory,
            ".dvmconsole-export-" + Guid.NewGuid().ToString("N"));
        AppDataFileProtection.EnsureDirectory(stagingDirectory);
        string name = Path.GetFileName(fullPath);
        destinations[name] = fullPath;
        Primary = new FileConfigurationDocument(
            Path.Combine(stagingDirectory, name),
            protectAsAppData: true);
    }

    public IWritableDocument Primary { get; }

    public ValueTask<IWritableDocument> CreateCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = EnsureSafeName(safeRelativeName);
        destinations[name] = Path.Combine(destinationDirectory, name);
        return ValueTask.FromResult<IWritableDocument>(
            new FileConfigurationDocument(
                Path.Combine(stagingDirectory, name),
                protectAsAppData: true));
    }

    public ValueTask<IReadableDocument?> ResolveExportedCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(stagingDirectory, EnsureSafeName(safeRelativeName));
        return ValueTask.FromResult<IReadableDocument?>(
            File.Exists(path) ? new FileConfigurationDocument(path) : null);
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            throw new InvalidOperationException("The export transaction has already completed.");

        using FileStream destinationLock = FileExportDestinationLock.Acquire(
            destinations.Values.First() + ".dvmconsole.lock");
        string backupDirectory = Path.Combine(stagingDirectory, ".backup");
        AppDataFileProtection.EnsureDirectory(backupDirectory);
        var installed = new List<string>();
        var backedUp = new List<(string Backup, string Destination)>();
        try
        {
            foreach ((string name, string destination) in destinations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string staged = Path.Combine(stagingDirectory, name);
                if (!File.Exists(staged))
                    throw new IOException($"Staged export file '{name}' is missing.");
                if (File.Exists(destination))
                {
                    string backup = Path.Combine(backupDirectory, name);
                    File.Move(destination, backup);
                    AppDataFileProtection.EnsureFile(backup);
                    backedUp.Add((backup, destination));
                }
                File.Move(staged, destination);
                AppDataFileProtection.EnsureFile(destination);
                installed.Add(destination);
            }

            Directory.Delete(stagingDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }
        catch
        {
            foreach (string path in installed.AsEnumerable().Reverse())
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            foreach ((string backup, string destination) in backedUp.AsEnumerable().Reverse())
            {
                if (File.Exists(backup))
                    File.Move(backup, destination, overwrite: true);
            }
            Interlocked.Exchange(ref completed, 0);
            throw;
        }
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref completed, 1) == 0 && Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
        => Volatile.Read(ref completed) == 0
            ? RollbackAsync(CancellationToken.None)
            : ValueTask.CompletedTask;

    private static string EnsureSafeName(string value)
    {
        string name = Path.GetFileName(value);
        if (name.Length == 0 || !string.Equals(name, value, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe companion name '{value}'.");
        return name;
    }
}

public sealed class FileConfigurationDocument(
    string path,
    bool protectAsAppData = false) : IWritableDocument
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public string DisplayName => System.IO.Path.GetFileName(Path);
    public string OriginIdentity => Path;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read));
    }

    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string parent = System.IO.Path.GetDirectoryName(Path) ?? AppContext.BaseDirectory;
        if (protectAsAppData)
            AppDataFileProtection.EnsureDirectory(parent);
        else
            Directory.CreateDirectory(parent);
        var stream = new FileStream(
            Path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        if (protectAsAppData)
            AppDataFileProtection.EnsureFile(Path);
        return ValueTask.FromResult<Stream>(stream);
    }
}

internal static class FileExportDestinationLock
{
    public static FileStream Acquire(string path)
    {
        long started = Environment.TickCount64;
        IOException? lastFailure = null;
        while (Environment.TickCount64 - started < TimeSpan.FromSeconds(5).TotalMilliseconds)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException exception)
            {
                lastFailure = exception;
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
        }

        throw new IOException(
            $"Timed out waiting for another DVM Console export to finish writing '{path}'.",
            lastFailure);
    }
}
