// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DvmConsole.Core.IO;
using DvmConsole.Core.Settings;
using System.Text;

namespace DvmConsole.Desktop;

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
