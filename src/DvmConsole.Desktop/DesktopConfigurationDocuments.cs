using DvmConsole.Application;
using Avalonia.Platform.Storage;

namespace DvmConsole.Desktop;

// Filesystem paths are intentionally confined to the desktop host. The
// configuration library itself works with document handles and streams.
internal sealed class DesktopConfigurationDocumentSet : IImportDocumentSet, IExportDocumentSet
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

    private string ResolveSafeCompanionPath(string name)
    {
        string fileName = Path.GetFileName(name);
        if (fileName.Length == 0 || !string.Equals(fileName, name, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe companion name '{name}'.");
        return Path.Combine(directory, fileName);
    }
}

internal sealed class DesktopConfigurationDocument(string path) : IWritableDocument
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
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? AppContext.BaseDirectory);
        return ValueTask.FromResult<Stream>(new FileStream(
            Path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None));
    }
}

internal sealed class DesktopConfigurationMaterializer(
    IConfigurationLibrary library,
    string runtimeRoot)
{
    private readonly IConfigurationLibrary library = library ?? throw new ArgumentNullException(nameof(library));
    private readonly string runtimeRoot = Path.GetFullPath(runtimeRoot);

    public async ValueTask<string> MaterializeAsync(
        ConfigurationReference configuration,
        CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(
            runtimeRoot,
            configuration.Id.ToString(),
            configuration.Revision.ToString());
        string codeplugPath = Path.Combine(directory, "codeplug.yml");
        Directory.CreateDirectory(directory);
        var destination = new DesktopConfigurationDocumentSet(codeplugPath);
        await library.ExportAsync(
            configuration,
            destination,
            new ConfigurationExportOptions(Sanitized: false, IncludeCompanions: true),
            cancellationToken).ConfigureAwait(false);
        return codeplugPath;
    }
}

// Document-handle adapter used by Studio export. It never calls
// TryGetLocalPath, so the same export flow remains viable for sandbox and
// content-URI pickers on future hosts.
internal sealed class AvaloniaStorageConfigurationDocumentSet : IExportDocumentSet, IDisposable
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

    public async ValueTask<IWritableDocument> CreateCompanionAsync(
        string safeRelativeName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = EnsureSafeName(safeRelativeName);
        IStorageFolder folder = await GetParentAsync().ConfigureAwait(false);
        IStorageFile file = await folder.CreateFileAsync(name).ConfigureAwait(false)
            ?? throw new IOException($"The selected export folder could not create companion '{name}'.");
        companions[name] = file;
        return new AvaloniaStorageConfigurationDocument(file);
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
            file = await folder.GetFileAsync(name).ConfigureAwait(false);
            if (file is null)
                return null;
            companions[name] = file;
        }
        return new AvaloniaStorageConfigurationDocument(file);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (IStorageFile companion in companions.Values.Distinct())
            companion.Dispose();
        parent?.Dispose();
        primary.Dispose();
    }

    private async Task<IStorageFolder> GetParentAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return parent ??= await primary.GetParentAsync().ConfigureAwait(false)
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

internal sealed class AvaloniaStorageConfigurationDocument(IStorageFile file) : IWritableDocument
{
    private readonly IStorageFile file = file ?? throw new ArgumentNullException(nameof(file));

    public string DisplayName => file.Name;
    public string? OriginIdentity => file.Path?.ToString();

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Stream>(file.OpenReadAsync());
    }

    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Stream>(file.OpenWriteAsync());
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
            previous.Dispose();
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
            return new AvaloniaStorageConfigurationDocument(selected);
        string[] segments = SafeSegments(relativeReference);
        if (segments.Length == 0)
            return null;

        IStorageFolder folder = await GetParentAsync().ConfigureAwait(false);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            IStorageFolder? child = await folder.GetFolderAsync(segments[index]).ConfigureAwait(false);
            if (child is null)
                return null;
            openedItems.Add(child);
            folder = child;
        }

        IStorageFile? file = await folder.GetFileAsync(segments[^1]).ConfigureAwait(false);
        if (file is null)
            return null;
        openedItems.Add(file);
        return new AvaloniaStorageConfigurationDocument(file);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (IDisposable item in openedItems.Distinct())
            item.Dispose();
        parent?.Dispose();
        primary.Dispose();
    }

    private async Task<IStorageFolder> GetParentAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return parent ??= await primary.GetParentAsync().ConfigureAwait(false)
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
