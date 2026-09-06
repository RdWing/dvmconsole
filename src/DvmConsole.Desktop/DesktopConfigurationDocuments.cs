// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DvmConsole.Core.IO;
using DvmConsole.Core.Settings;
using System.Text;

namespace DvmConsole.Desktop;

// Filesystem paths are intentionally confined to the desktop host. The
// configuration library itself works with document handles and streams.
internal sealed class DesktopConfigurationDocumentSet :
    IImportDocumentSet,
    ITransactionalExportDocumentSet
{
    private readonly string directory;

    public DesktopConfigurationDocumentSet(string primaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        string fullPath = Path.GetFullPath(primaryPath);
        directory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        PrimaryDocument = new DesktopConfigurationDocument(fullPath);
    }

    public DesktopConfigurationDocument PrimaryDocument { get; }
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
            File.Exists(path) ? new DesktopConfigurationDocument(path) : null);
    }

    public ValueTask<IWritableDocument> CreateCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ResolveSafeCompanionPath(safeRelativeName);
        return ValueTask.FromResult<IWritableDocument>(new DesktopConfigurationDocument(path));
    }

    public ValueTask<IReadableDocument?> ResolveExportedCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ResolveSafeCompanionPath(safeRelativeName);
        return ValueTask.FromResult<IReadableDocument?>(
            File.Exists(path) ? new DesktopConfigurationDocument(path) : null);
    }

    public ValueTask<IExportDocumentTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IExportDocumentTransaction>(
            new DesktopConfigurationExportTransaction(PrimaryDocument.Path));
    }

    private string ResolveSafeCompanionPath(string name)
    {
        string fileName = Path.GetFileName(name);
        if (fileName.Length == 0 || !string.Equals(fileName, name, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe companion name '{name}'.");
        return Path.Combine(directory, fileName);
    }
}

internal sealed class DesktopConfigurationExportTransaction : IExportDocumentTransaction
{
    private readonly string destinationDirectory;
    private readonly string stagingDirectory;
    private readonly Dictionary<string, string> destinations = new(StringComparer.OrdinalIgnoreCase);
    private int completed;

    public DesktopConfigurationExportTransaction(string primaryPath)
    {
        string fullPath = Path.GetFullPath(primaryPath);
        destinationDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        stagingDirectory = Path.Combine(
            destinationDirectory,
            ".dvmconsole-export-" + Guid.NewGuid().ToString("N"));
        AppDataFileProtection.EnsureDirectory(stagingDirectory);
        string name = Path.GetFileName(fullPath);
        destinations[name] = fullPath;
        Primary = new DesktopConfigurationDocument(
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
            new DesktopConfigurationDocument(
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
            File.Exists(path) ? new DesktopConfigurationDocument(path) : null);
    }

    public ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            throw new InvalidOperationException("The export transaction has already completed.");

        using FileStream destinationLock = DesktopExportDestinationLock.Acquire(
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

internal sealed class DesktopConfigurationDocument(
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

internal static class DesktopExportDestinationLock
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

// Gives a dirty Studio export a self-contained view of the current in-memory
// YAML and companion bytes. An imported document's original path may no longer
// exist, and the managed Studio draft must never depend on that path.
internal sealed class ConfigurationStudioExportDocumentSet(
    string yaml,
    string displayName,
    IReadOnlyDictionary<string, string> companionContents) : IImportDocumentSet
{
    private readonly IReadOnlyDictionary<string, string> companionContents =
        companionContents ?? throw new ArgumentNullException(nameof(companionContents));

    public IReadableDocument Primary { get; } = new InMemoryConfigurationDocument(
        string.IsNullOrWhiteSpace(displayName) ? "codeplug.yml" : displayName,
        "primary",
        Encoding.UTF8.GetBytes(yaml ?? throw new ArgumentNullException(nameof(yaml))));

    public ValueTask<IReadableDocument?> ResolveCompanionAsync(
        string relativeReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return companionContents.TryGetValue(relativeReference, out string? content)
            ? ValueTask.FromResult<IReadableDocument?>(new InMemoryConfigurationDocument(
                Path.GetFileName(relativeReference),
                relativeReference,
                Encoding.UTF8.GetBytes(content)))
            : ValueTask.FromResult<IReadableDocument?>(null);
    }

    private sealed class InMemoryConfigurationDocument(
        string displayName,
        string originIdentity,
        byte[] content) : IReadableDocument
    {
        public string DisplayName { get; } = displayName;
        public string OriginIdentity { get; } = "studio-draft:" + originIdentity;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }
}

internal sealed class DesktopConfigurationMaterializer
{
    private readonly IConfigurationLibrary library;
    private readonly string runtimeRoot;

    public DesktopConfigurationMaterializer(
        IConfigurationLibrary library,
        string runtimeRoot,
        bool removeOrphans = true)
    {
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.runtimeRoot = Path.GetFullPath(runtimeRoot);
        AppDataFileProtection.EnsureDirectory(this.runtimeRoot);
        if (removeOrphans)
            RemoveOrphanedMaterializations();
    }

    public async ValueTask<IConfigurationMaterializationLease> MaterializeAsync(
        ConfigurationReference configuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DesktopConfigurationMaterializationLease lease;
        using (CrossProcessStoreLock.Acquire(Path.Combine(runtimeRoot, ".runtime.lock")))
        {
            string directory = Path.Combine(runtimeRoot, "session-" + Guid.NewGuid().ToString("N"));
            AppDataFileProtection.EnsureDirectory(directory);
            lease = DesktopConfigurationMaterializationLease.Create(directory);
        }

        try
        {
            var destination = new DesktopConfigurationDocumentSet(lease.Path);
            await library.ExportAsync(
                configuration,
                destination,
                new ConfigurationExportOptions(Sanitized: false, IncludeCompanions: true),
                cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void RemoveOrphanedMaterializations()
    {
        using FileStream rootLock = CrossProcessStoreLock.Acquire(Path.Combine(runtimeRoot, ".runtime.lock"));
        foreach (string directory in Directory.EnumerateDirectories(runtimeRoot))
        {
            string name = Path.GetFileName(directory);
            if (!name.StartsWith("session-", StringComparison.Ordinal))
            {
                RemoveLegacyOrphan(directory);
                continue;
            }

            string leasePath = Path.Combine(directory, DesktopConfigurationMaterializationLease.LeaseFileName);
            FileStream? probe = null;
            try
            {
                probe = new FileStream(
                    leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                AppDataFileProtection.EnsureFile(leasePath);
            }
            catch (IOException)
            {
                continue;
            }

            probe.Dispose();
            TryDeleteMaterialization(directory);
        }
    }

    private static void RemoveLegacyOrphan(string directory)
    {
        // Older builds used <configuration>/<revision> folders and never held
        // a lease. A top-level directory in this app-owned runtime root is
        // therefore safe to treat as an orphan during the one-time migration.
        TryDeleteMaterialization(directory);
    }

    internal static void TryDeleteMaterialization(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A concurrent process may still have a document open. Its lease,
            // or the next startup pass, remains responsible for cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave the scoped runtime directory for a later repair pass.
        }
    }
}

internal interface IConfigurationMaterializationLease : IAsyncDisposable
{
    string Path { get; }
}

internal sealed class DesktopConfigurationMaterializationLease : IConfigurationMaterializationLease
{
    internal const string LeaseFileName = ".lease";
    private readonly string directory;
    private FileStream? leaseStream;

    private DesktopConfigurationMaterializationLease(
        string directory,
        string path,
        FileStream leaseStream)
    {
        this.directory = directory;
        Path = path;
        this.leaseStream = leaseStream;
    }

    public string Path { get; }

    internal static DesktopConfigurationMaterializationLease Create(string directory)
    {
        string fullDirectory = System.IO.Path.GetFullPath(directory);
        string leasePath = System.IO.Path.Combine(fullDirectory, LeaseFileName);
        var leaseStream = new FileStream(
            leasePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        AppDataFileProtection.EnsureFile(leasePath);
        return new DesktopConfigurationMaterializationLease(
            fullDirectory,
            System.IO.Path.Combine(fullDirectory, "codeplug.yml"),
            leaseStream);
    }

    public ValueTask DisposeAsync()
    {
        FileStream? owned = Interlocked.Exchange(ref leaseStream, null);
        if (owned is null)
            return ValueTask.CompletedTask;
        owned.Dispose();
        DesktopConfigurationMaterializer.TryDeleteMaterialization(directory);
        return ValueTask.CompletedTask;
    }
}

// Document-handle adapter used by Studio export. It never calls
// TryGetLocalPath, so the same export flow remains viable for sandbox and
// content-URI pickers on future hosts.
internal sealed class AvaloniaStorageConfigurationDocumentSet :
    ITransactionalExportDocumentSet,
    IDisposable
{
    private readonly IStorageFile primary;
    private readonly AvaloniaStorageConfigurationDocument primaryDocument;
    private readonly Dictionary<string, IStorageFile> companions =
        new(StringComparer.OrdinalIgnoreCase);
    private IStorageFolder? parent;
    private int disposed;

    public AvaloniaStorageConfigurationDocumentSet(IStorageFile primary)
    {
        this.primary = primary ?? throw new ArgumentNullException(nameof(primary));
        primaryDocument = new AvaloniaStorageConfigurationDocument(primary);
    }

    public IWritableDocument Primary => primaryDocument;

    public ValueTask<IExportDocumentTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IExportDocumentTransaction>(
            new AvaloniaStorageExportTransaction(primary, GetParentAsync));
    }

    public async ValueTask<IWritableDocument> CreateCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = EnsureSafeName(safeRelativeName);
        IStorageFolder folder = await GetParentAsync().ConfigureAwait(false);
        IStorageFile file = await AvaloniaStorageThreading
            .InvokeAsync(() => folder.CreateFileAsync(name))
            .ConfigureAwait(false)
            ?? throw new IOException($"The selected export folder could not create companion '{name}'.");
        companions[name] = file;
        return await AvaloniaStorageThreading
            .Invoke(() => (IWritableDocument)new AvaloniaStorageConfigurationDocument(file))
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadableDocument?> ResolveExportedCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = EnsureSafeName(safeRelativeName);
        if (!companions.TryGetValue(name, out IStorageFile? file))
        {
            IStorageFolder folder = await GetParentAsync().ConfigureAwait(false);
            file = await AvaloniaStorageThreading
                .InvokeAsync(() => folder.GetFileAsync(name))
                .ConfigureAwait(false);
            if (file is null)
                return null;
            companions[name] = file;
        }
        return await AvaloniaStorageThreading
            .Invoke(() => (IReadableDocument)new AvaloniaStorageConfigurationDocument(file))
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        AvaloniaStorageThreading.Invoke(() =>
        {
            foreach (IStorageFile companion in companions.Values.Distinct())
                companion.Dispose();
            parent?.Dispose();
            primary.Dispose();
        });
    }

    private async Task<IStorageFolder> GetParentAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return parent ??= await AvaloniaStorageThreading
            .InvokeAsync(primary.GetParentAsync)
            .ConfigureAwait(false)
            ?? throw new IOException("The selected export document did not expose a parent folder for companion files.");
    }

    private static string EnsureSafeName(string value)
    {
        string name = Path.GetFileName(value);
        if (name.Length == 0 || !string.Equals(name, value, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe companion name '{value}'.");
        return name;
    }
}

internal sealed class AvaloniaStorageExportTransaction : IExportDocumentTransaction
{
    private readonly IStorageFile primaryFile;
    private readonly Func<Task<IStorageFolder>> getParentAsync;
    private readonly BufferedConfigurationDocument primary;
    private readonly Dictionary<string, BufferedConfigurationDocument> companions =
        new(StringComparer.OrdinalIgnoreCase);
    private int completed;

    public AvaloniaStorageExportTransaction(
        IStorageFile primaryFile,
        Func<Task<IStorageFolder>> getParentAsync)
    {
        this.primaryFile = primaryFile ?? throw new ArgumentNullException(nameof(primaryFile));
        this.getParentAsync = getParentAsync ?? throw new ArgumentNullException(nameof(getParentAsync));
        primary = new BufferedConfigurationDocument(primaryFile.Name, primaryFile.Path?.ToString());
    }

    public IWritableDocument Primary => primary;

    public ValueTask<IWritableDocument> CreateCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = EnsureSafeName(safeRelativeName);
        var document = new BufferedConfigurationDocument(name, "pending-picker:" + name);
        companions[name] = document;
        return ValueTask.FromResult<IWritableDocument>(document);
    }

    public ValueTask<IReadableDocument?> ResolveExportedCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        companions.TryGetValue(EnsureSafeName(safeRelativeName), out BufferedConfigurationDocument? document);
        return ValueTask.FromResult<IReadableDocument?>(document);
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
            throw new InvalidOperationException("The export transaction has already completed.");

        byte[] primaryContent = primary.GetContent();
        IStorageFolder parent = await getParentAsync().ConfigureAwait(false);
        var targets = new List<StorageWriteTarget>
        {
            new(
                primaryFile,
                primaryContent,
                await ReadExistingOrMissingAsync(
                    primaryFile.Name,
                    () => AvaloniaStorageThreading.InvokeAsync(primaryFile.OpenReadAsync),
                    cancellationToken).ConfigureAwait(false))
        };
        try
        {
            foreach ((string name, BufferedConfigurationDocument document) in companions)
            {
                IStorageFile? existing = await AvaloniaStorageThreading
                    .InvokeAsync(() => parent.GetFileAsync(name))
                    .ConfigureAwait(false);
                byte[]? original = existing is null
                    ? null
                    : await ReadExistingAsync(existing, cancellationToken).ConfigureAwait(false);
                IStorageFile target = existing ?? await AvaloniaStorageThreading
                    .InvokeAsync(() => parent.CreateFileAsync(name))
                    .ConfigureAwait(false)
                    ?? throw new IOException($"The selected export folder could not create companion '{name}'.");
                targets.Add(new StorageWriteTarget(target, document.GetContent(), original));
            }

            foreach (StorageWriteTarget target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteAsync(target.File, target.Content, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exportFailure)
        {
            try
            {
                await RestoreAsync(targets).ConfigureAwait(false);
            }
            catch (Exception restoreFailure)
            {
                throw new AggregateException(
                    "The picker export failed and rollback was incomplete.",
                    exportFailure,
                    restoreFailure);
            }
            Interlocked.Exchange(ref completed, 0);
            throw;
        }
        finally
        {
            foreach (StorageWriteTarget target in targets.Skip(1))
                target.File.Dispose();
        }
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref completed, 1);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
        => Volatile.Read(ref completed) == 0
            ? RollbackAsync(CancellationToken.None)
            : ValueTask.CompletedTask;

    private static async Task<byte[]> ReadExistingAsync(
        IStorageFile file,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await AvaloniaStorageThreading
            .InvokeAsync(file.OpenReadAsync)
            .ConfigureAwait(false);
        return await BoundedResourceReader.ReadBytesAsync(
            stream,
            ManagedResourceLimits.ConfigurationCompanionBytes,
            file.Name,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]?> ReadExistingOrMissingAsync(
        string displayName,
        Func<Task<Stream>> openReadAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(openReadAsync);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using Stream stream = await openReadAsync().ConfigureAwait(false);
            return await BoundedResourceReader.ReadBytesAsync(
                stream,
                ManagedResourceLimits.ConfigurationCompanionBytes,
                displayName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Save pickers commonly represent a new file with an IStorageFile
            // handle before the path exists. There is nothing to preserve for
            // rollback in that case; the commit will create the destination.
            return null;
        }
    }

    private static async Task WriteAsync(
        IStorageFile file,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await AvaloniaStorageThreading
            .InvokeAsync(file.OpenWriteAsync)
            .ConfigureAwait(false);
        if (stream.CanSeek)
            stream.SetLength(0);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RestoreAsync(IReadOnlyList<StorageWriteTarget> targets)
    {
        List<Exception>? failures = null;
        foreach (StorageWriteTarget target in targets.Reverse())
        {
            try
            {
                if (target.OriginalContent is null)
                    await AvaloniaStorageThreading.InvokeAsync(target.File.DeleteAsync).ConfigureAwait(false);
                else
                    await WriteAsync(target.File, target.OriginalContent, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        if (failures is not null)
            throw new AggregateException("The export failed and one or more picker documents could not be restored.", failures);
    }

    private static string EnsureSafeName(string value)
    {
        string name = Path.GetFileName(value);
        if (name.Length == 0 || !string.Equals(name, value, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe companion name '{value}'.");
        return name;
    }

    private sealed record StorageWriteTarget(
        IStorageFile File,
        byte[] Content,
        byte[]? OriginalContent);
}

internal sealed class BufferedConfigurationDocument(
    string displayName,
    string? originIdentity) : IWritableDocument
{
    private byte[]? content;

    public string DisplayName { get; } = displayName;
    public string? OriginIdentity { get; } = originIdentity;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new MemoryStream(GetContent(), writable: false));
    }

    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new BufferedWriteStream(bytes => content = bytes));
    }

    internal byte[] GetContent()
        => content?.ToArray() ?? throw new IOException($"Export document '{DisplayName}' was not written.");

    private sealed class BufferedWriteStream(Action<byte[]> commit) : MemoryStream
    {
        private int committed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref committed, 1) == 0)
                commit(ToArray());
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref committed, 1) == 0)
                commit(ToArray());
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class AvaloniaStorageConfigurationDocument(IStorageFile file) : IWritableDocument
{
    private readonly IStorageFile file = file ?? throw new ArgumentNullException(nameof(file));
    private readonly string displayName = file.Name;
    private readonly string? originIdentity = file.Path?.ToString();

    public string DisplayName => displayName;
    public string? OriginIdentity => originIdentity;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Stream>(
            AvaloniaStorageThreading.InvokeAsync(file.OpenReadAsync));
    }

    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Stream>(
            AvaloniaStorageThreading.InvokeAsync(file.OpenWriteAsync));
    }
}

internal static class AvaloniaStorageThreading
{
    public static void Invoke(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Dispatcher.UIThread.CheckAccess())
        {
            operation();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                operation();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        completion.Task.GetAwaiter().GetResult();
    }

    public static Task<T> Invoke<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return InvokeAsync(() => Task.FromResult(operation()));
    }

    public static Task InvokeAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Dispatcher.UIThread.CheckAccess())
            return operation();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await operation();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    public static Task<T> InvokeAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Dispatcher.UIThread.CheckAccess())
            return operation();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await operation());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }
}

/// <summary>
/// Imports a picker-owned configuration without requiring a local path. Only
/// safe descendants of the primary document's folder are resolved
/// automatically; external companions remain an explicit import decision.
/// </summary>
internal sealed class AvaloniaStorageConfigurationImportDocumentSet : IImportDocumentSet, IDisposable
{
    private readonly IStorageFile primary;
    private readonly AvaloniaStorageConfigurationDocument primaryDocument;
    private readonly Dictionary<string, IStorageFile> explicitlySelectedCompanions =
        new(StringComparer.Ordinal);
    private readonly List<IDisposable> openedItems = [];
    private IStorageFolder? parent;
    private int disposed;

    public AvaloniaStorageConfigurationImportDocumentSet(IStorageFile primary)
    {
        this.primary = primary ?? throw new ArgumentNullException(nameof(primary));
        primaryDocument = new AvaloniaStorageConfigurationDocument(primary);
    }

    public IReadableDocument Primary => primaryDocument;

    public void AddExplicitCompanion(string reference, IStorageFile file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(file);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (explicitlySelectedCompanions.TryGetValue(reference, out IStorageFile? previous))
        {
            openedItems.Remove(previous);
            AvaloniaStorageThreading.Invoke(previous.Dispose);
        }
        explicitlySelectedCompanions[reference] = file;
        openedItems.Add(file);
    }

    public async ValueTask<IReadableDocument?> ResolveCompanionAsync(
        string relativeReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (explicitlySelectedCompanions.TryGetValue(relativeReference, out IStorageFile? selected))
        {
            return await AvaloniaStorageThreading
                .Invoke(() => (IReadableDocument)new AvaloniaStorageConfigurationDocument(selected))
                .ConfigureAwait(false);
        }
        string[] segments = SafeSegments(relativeReference);
        if (segments.Length == 0)
            return null;

        IStorageFolder folder = await GetParentAsync().ConfigureAwait(false);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            IStorageFolder? child = await AvaloniaStorageThreading
                .InvokeAsync(() => folder.GetFolderAsync(segments[index]))
                .ConfigureAwait(false);
            if (child is null)
                return null;
            openedItems.Add(child);
            folder = child;
        }

        IStorageFile? file = await AvaloniaStorageThreading
            .InvokeAsync(() => folder.GetFileAsync(segments[^1]))
            .ConfigureAwait(false);
        if (file is null)
            return null;
        openedItems.Add(file);
        return await AvaloniaStorageThreading
            .Invoke(() => (IReadableDocument)new AvaloniaStorageConfigurationDocument(file))
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        AvaloniaStorageThreading.Invoke(() =>
        {
            foreach (IDisposable item in openedItems.Distinct())
                item.Dispose();
            parent?.Dispose();
            primary.Dispose();
        });
    }

    private async Task<IStorageFolder> GetParentAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return parent ??= await AvaloniaStorageThreading
            .InvokeAsync(primary.GetParentAsync)
            .ConfigureAwait(false)
            ?? throw new IOException("The selected configuration did not expose a folder for companion files.");
    }

    private static string[] SafeSegments(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference))
            return [];
        string normalized = reference.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment != "..")
            ? segments
            : [];
    }
}
