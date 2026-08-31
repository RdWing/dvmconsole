using System.Text.Json;
using DvmConsole.Application;

namespace DvmConsole.Storage;

public sealed class ManagedAssetStore : IAssetStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string root;
    private readonly string catalogPath;

    public ManagedAssetStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        root = Path.GetFullPath(rootPath);
        catalogPath = Path.Combine(root, "catalog.json");
        Directory.CreateDirectory(Path.Combine(root, "content"));
        if (!File.Exists(catalogPath))
            WriteCatalog([]);
    }

    public async ValueTask<AssetDescriptor> ImportAsync(
        string displayName,
        string mediaType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentNullException.ThrowIfNull(content);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssetId id = AssetId.New();
            string destination = ContentPath(id);
            string pending = destination + ".pending";
            await using (var output = new FileStream(
                             pending,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(pending, destination);
            var descriptor = new AssetDescriptor(
                id,
                displayName.Trim(),
                mediaType.Trim(),
                new FileInfo(destination).Length);
            List<AssetDescriptor> catalog = ReadCatalog();
            catalog.Add(descriptor);
            WriteCatalog(catalog);
            return descriptor;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<Stream> OpenReadAsync(
        AssetId id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReadCatalog().Any(asset => asset.Id == id))
                throw new KeyNotFoundException($"Asset '{id}' is not in the managed store.");
            return new FileStream(
                ContentPath(id),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous);
        }
        finally
        {
            gate.Release();
        }
    }

    public async IAsyncEnumerable<AssetDescriptor> ListAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetDescriptor[] snapshot;
        try
        {
            snapshot = ReadCatalog().OrderBy(asset => asset.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            gate.Release();
        }
        foreach (AssetDescriptor descriptor in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return descriptor;
        }
    }

    private string ContentPath(AssetId id) => Path.Combine(root, "content", id.Value.ToString("N") + ".asset");

    private List<AssetDescriptor> ReadCatalog()
        => JsonSerializer.Deserialize(File.ReadAllText(catalogPath), StorageJsonContext.Default.ListAssetDescriptor) ?? [];

    private void WriteCatalog(List<AssetDescriptor> catalog)
        => AtomicJsonFile.Write(catalogPath, catalog, StorageJsonContext.Default.ListAssetDescriptor);
}
