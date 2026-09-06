// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;
using Avalonia.Media.Imaging;
using DvmConsole.Application;
using DvmConsole.Core.Settings;
using DvmConsole.Storage;

namespace DvmConsole.Desktop;

// Owns the background image's managed-asset, bitmap, presentation, and
// cancellation lifetime. The shell facade only forwards state changes.
internal sealed class BackgroundAppearanceController : IAsyncDisposable
{
    private readonly IAssetStore assetStore;
    private readonly UserSettings settings;
    private readonly IUiDispatcher dispatcher;
    private readonly Action persistSettings;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly AsyncDisposal disposal = new();
    private readonly object operationSync = new();
    private readonly HashSet<Task> activeOperations = [];
    private Task initialLoadTask = Task.CompletedTask;
    private bool initialLoadStarted;
    private Bitmap? bitmap;
    private IBrush brush;
    private int disposalStarted;

    public BackgroundAppearanceController(
        IAssetStore assetStore,
        UserSettings settings,
        IUiDispatcher dispatcher,
        Action persistSettings)
    {
        this.assetStore = assetStore ?? throw new ArgumentNullException(nameof(assetStore));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.persistSettings = persistSettings ?? throw new ArgumentNullException(nameof(persistSettings));
        brush = CreateShellBrush(settings.DarkMode);
    }

    public event EventHandler? Changed;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? Warning;

    public IBrush Brush => brush;
    public string? Reference => settings.UserBackgroundAssetId ?? settings.UserBackgroundImage;

    public void BeginInitialLoad()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
        if (initialLoadStarted)
            throw new InvalidOperationException("Background loading has already started.");

        initialLoadStarted = true;
        initialLoadTask = Task.Run(
            () => LoadInitialAsync(
                settings.UserBackgroundImage,
                lifetimeCancellation.Token),
            CancellationToken.None);
        TaskObservation.Observe(initialLoadTask);
    }

    public Task PrepareAsync(CancellationToken cancellationToken)
        => initialLoadTask.WaitAsync(cancellationToken);

    public Task<bool> SetAsync(
        string displayName,
        string mediaType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentNullException.ThrowIfNull(content);
        return TrackOperation(() => SetCoreAsync(
            displayName,
            mediaType,
            content,
            cancellationToken));
    }

    private async Task<bool> SetCoreAsync(
        string displayName,
        string mediaType,
        Stream content,
        CancellationToken cancellationToken)
    {
        AssetDescriptor? imported = null;
        Bitmap? candidate = null;
        string? previousAssetId = settings.UserBackgroundAssetId;
        string? previousImagePath = settings.UserBackgroundImage;
        bool settingsUpdated = false;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        CancellationToken operationToken = operationCancellation.Token;
        try
        {
            imported = await assetStore.ImportAsync(
                displayName,
                mediaType,
                content,
                operationToken).ConfigureAwait(false);
            using Stream stored = await assetStore.OpenReadAsync(imported.Id, operationToken)
                .ConfigureAwait(false);
            candidate = new Bitmap(stored);
            operationToken.ThrowIfCancellationRequested();
            settings.UserBackgroundAssetId = imported.Id.ToString();
            settings.UserBackgroundImage = null;
            try
            {
                persistSettings();
                settingsUpdated = true;
            }
            catch
            {
                settings.UserBackgroundAssetId = previousAssetId;
                settings.UserBackgroundImage = previousImagePath;
                throw;
            }

            Bitmap published = candidate;
            bool accepted = false;
            await dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref disposalStarted) != 0 || operationToken.IsCancellationRequested)
                    return;
                PublishBitmap(published);
                accepted = true;
            }).ConfigureAwait(false);
            if (!accepted)
                throw new OperationCanceledException(operationToken);
            candidate = null;
            await PublishStatusAsync($"Background loaded: {displayName}.").ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            candidate?.Dispose();
            if (settingsUpdated)
                RestorePersistedReference(previousAssetId, previousImagePath);
            if (imported is not null)
                await TryDeleteUnreferencedAsync(imported.Id, new HashSet<AssetId>()).ConfigureAwait(false);
            return false;
        }
        catch (Exception exception)
        {
            candidate?.Dispose();
            if (settingsUpdated)
                RestorePersistedReference(previousAssetId, previousImagePath);
            if (imported is not null)
                await TryDeleteUnreferencedAsync(imported.Id, new HashSet<AssetId>()).ConfigureAwait(false);
            await PublishStatusAsync($"Background unavailable: {exception.Message}").ConfigureAwait(false);
            return false;
        }
    }

    public void Clear()
    {
        lock (operationSync)
            ObjectDisposedException.ThrowIf(disposalStarted != 0, this);
        string? removedAssetId = settings.UserBackgroundAssetId;
        bitmap?.Dispose();
        bitmap = null;
        brush = CreateShellBrush(settings.DarkMode);
        settings.UserBackgroundImage = null;
        settings.UserBackgroundAssetId = null;
        persistSettings();
        TaskObservation.Observe(DeleteIfUnreferencedAsync(removedAssetId).AsTask());
        Changed?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, "User background cleared.");
    }

    public void ApplyTheme(bool darkMode)
    {
        if (bitmap is not null)
            return;

        brush = CreateShellBrush(darkMode);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync() => disposal.RunAsync(DisposeCoreAsync);

    private async Task LoadInitialAsync(string? legacyPath, CancellationToken cancellationToken)
    {
        Bitmap? candidate = null;
        AssetDescriptor? imported = null;
        string? previousAssetId = settings.UserBackgroundAssetId;
        string? previousImagePath = settings.UserBackgroundImage;
        bool settingsUpdated = false;
        try
        {
            Stream? content = null;
            if (Guid.TryParse(settings.UserBackgroundAssetId, out Guid assetId))
            {
                content = await assetStore.OpenReadAsync(new AssetId(assetId), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(legacyPath) && File.Exists(legacyPath))
            {
                await using FileStream source = new(
                    legacyPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    useAsync: true);
                imported = await assetStore.ImportAsync(
                    Path.GetFileName(legacyPath),
                    GetImageMediaType(legacyPath),
                    source,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                settings.UserBackgroundAssetId = imported.Id.ToString();
                settings.UserBackgroundImage = null;
                try
                {
                    persistSettings();
                    settingsUpdated = true;
                }
                catch
                {
                    settings.UserBackgroundAssetId = previousAssetId;
                    settings.UserBackgroundImage = previousImagePath;
                    throw;
                }
                content = await assetStore.OpenReadAsync(imported.Id, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (content is null)
                return;
            using (content)
                candidate = new Bitmap(content);
            cancellationToken.ThrowIfCancellationRequested();
            await dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref disposalStarted) != 0)
                    return;
                PublishBitmap(candidate);
                candidate = null;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (settingsUpdated)
                RestorePersistedReference(previousAssetId, previousImagePath);
            if (imported is not null)
                await TryDeleteUnreferencedAsync(imported.Id, new HashSet<AssetId>()).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await PublishWarningAsync(
                $"Background asset could not be loaded: {exception.Message}").ConfigureAwait(false);
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private void RestorePersistedReference(string? assetId, string? imagePath)
    {
        settings.UserBackgroundAssetId = assetId;
        settings.UserBackgroundImage = imagePath;
        try
        {
            persistSettings();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Background reference rollback could not be persisted: {0}",
                exception);
        }
    }

    private void PublishBitmap(Bitmap candidate)
    {
        bitmap?.Dispose();
        bitmap = candidate;
        brush = new ImageBrush(bitmap)
        {
            Stretch = Stretch.UniformToFill,
            Opacity = 0.22
        };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal ValueTask DeleteIfUnreferencedAsync(string? removedAssetId)
        => new(TrackOperation(() => DeleteIfUnreferencedCoreAsync(removedAssetId)));

    private async Task DeleteIfUnreferencedCoreAsync(string? removedAssetId)
    {
        if (!Guid.TryParse(removedAssetId, out Guid removedId))
            return;

        var referenced = new HashSet<AssetId>();
        if (Guid.TryParse(settings.UserBackgroundAssetId, out Guid backgroundId))
            referenced.Add(new AssetId(backgroundId));
        foreach (AlertToneSetting tone in settings.AlertTones)
        {
            if (Guid.TryParse(tone.AssetId, out Guid toneId))
                referenced.Add(new AssetId(toneId));
        }

        await TryDeleteUnreferencedAsync(new AssetId(removedId), referenced).ConfigureAwait(false);
    }

    private async ValueTask TryDeleteUnreferencedAsync(
        AssetId assetId,
        IReadOnlySet<AssetId> referenced)
    {
        try
        {
            await assetStore.DeleteIfUnreferencedAsync(assetId, referenced).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await PublishWarningAsync(
                $"Could not remove unreferenced managed asset {assetId.Value:N}: {exception.Message}")
                .ConfigureAwait(false);
        }
    }

    private ValueTask PublishStatusAsync(string message)
        => dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref disposalStarted) == 0)
                StatusChanged?.Invoke(this, message);
        });

    private ValueTask PublishWarningAsync(string message)
        => dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref disposalStarted) == 0)
                Warning?.Invoke(this, message);
        });

    private async Task DisposeCoreAsync()
    {
        Task[] operations;
        lock (operationSync)
        {
            Volatile.Write(ref disposalStarted, 1);
            operations = activeOperations.ToArray();
        }
        lifetimeCancellation.Cancel();

        Exception? failure = null;
        try
        {
            await initialLoadTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        lifetimeCancellation.Dispose();
        bitmap?.Dispose();
        bitmap = null;

        if (failure is not null)
            throw failure;
    }

    private Task TrackOperation(Func<Task> createOperation)
    {
        ArgumentNullException.ThrowIfNull(createOperation);
        lock (operationSync)
        {
            ObjectDisposedException.ThrowIf(disposalStarted != 0, this);
            Task operation = Task.Run(createOperation, CancellationToken.None);
            activeOperations.Add(operation);
            TaskObservation.Observe(RetireOperationAsync(operation));
            return operation;
        }
    }

    private Task<T> TrackOperation<T>(Func<Task<T>> createOperation)
    {
        ArgumentNullException.ThrowIfNull(createOperation);
        lock (operationSync)
        {
            ObjectDisposedException.ThrowIf(disposalStarted != 0, this);
            Task<T> operation = Task.Run(createOperation, CancellationToken.None);
            activeOperations.Add(operation);
            TaskObservation.Observe(RetireOperationAsync(operation));
            return operation;
        }
    }

    private async Task RetireOperationAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch
        {
            // The operation's caller owns its result. This observer exists to
            // retire ownership even when a fire-and-forget cleanup faults.
        }
        finally
        {
            lock (operationSync)
                activeOperations.Remove(operation);
        }
    }

    internal static string GetImageMediaType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

    private static IBrush CreateShellBrush(bool darkMode)
        => SolidBrushCache.Get(darkMode ? "#0D1116" : "#F3F5F7");
}
