// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.IO;
using DvmConsole.Core.Settings;

namespace DvmConsole.Storage;

public sealed class ManagedConfigurationMaterializer
{
    private readonly IConfigurationLibrary library;
    private readonly string runtimeRoot;

    public ManagedConfigurationMaterializer(
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

    public ValueTask<IConfigurationMaterializationLease> MaterializeAsync(
        ConfigurationReference configuration,
        CancellationToken cancellationToken = default)
        => MaterializeCoreAsync((destination, token) => library.ExportAsync(configuration, destination,
            new ConfigurationExportOptions(Sanitized: false, IncludeCompanions: true), token), cancellationToken);

    public async ValueTask<(ConfigurationDraft Draft, IConfigurationMaterializationLease Lease)> MaterializeDraftAsync(
        ConfigurationId id, CancellationToken cancellationToken = default)
    {
        if (library is not IConfigurationDraftExporter exporter)
            throw new NotSupportedException("This configuration library cannot recover managed drafts.");
        ConfigurationDraft? draft = null;
        IConfigurationMaterializationLease lease = await MaterializeCoreAsync(async (destination, token) =>
        {
            draft = await exporter.ExportDraftAsync(id, destination,
                new ConfigurationExportOptions(Sanitized: false, IncludeCompanions: true), token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return (draft!, lease);
    }

    private async ValueTask<IConfigurationMaterializationLease> MaterializeCoreAsync(
        Func<IExportDocumentSet, CancellationToken, ValueTask> export, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ManagedConfigurationMaterializationLease lease;
        using (CrossProcessStoreLock.Acquire(Path.Combine(runtimeRoot, ".runtime.lock")))
        {
            string directory = Path.Combine(runtimeRoot, "session-" + Guid.NewGuid().ToString("N"));
            AppDataFileProtection.EnsureDirectory(directory);
            lease = ManagedConfigurationMaterializationLease.Create(directory);
        }

        try
        {
            var destination = new FileConfigurationDocumentSet(lease.Path);
            await export(destination, cancellationToken).ConfigureAwait(false);
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

            string leasePath = Path.Combine(directory, ManagedConfigurationMaterializationLease.LeaseFileName);
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

public interface IConfigurationMaterializationLease : IAsyncDisposable
{
    string Path { get; }
}

internal sealed class ManagedConfigurationMaterializationLease : IConfigurationMaterializationLease
{
    internal const string LeaseFileName = ".lease";
    private readonly string directory;
    private FileStream? leaseStream;

    private ManagedConfigurationMaterializationLease(
        string directory,
        string path,
        FileStream leaseStream)
    {
        this.directory = directory;
        Path = path;
        this.leaseStream = leaseStream;
    }

    public string Path { get; }

    internal static ManagedConfigurationMaterializationLease Create(string directory)
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
        return new ManagedConfigurationMaterializationLease(
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
        ManagedConfigurationMaterializer.TryDeleteMaterialization(directory);
        return ValueTask.CompletedTask;
    }
}

// Document-handle adapter used by Studio export. It never calls
// TryGetLocalPath, so the same export flow remains viable for sandbox and
// content-URI pickers on future hosts.
