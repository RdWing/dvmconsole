// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using DvmConsole.Platform.Dialogs;

namespace DvmConsole.Avalonia.Dialogs
{
    /// <summary>
    /// <see cref="IFileDialogService"/> adapter over Avalonia's
    /// <see cref="IStorageProvider"/>. The provider is injected (normally the
    /// owning window's <see cref="Avalonia.Controls.TopLevel.StorageProvider"/>)
    /// and is never looked up from a global TopLevel or window; the service
    /// never touches files, displays, secrets, or native pickers directly.
    ///
    /// Every dialog outcome (selection, user cancel, provider cancellation,
    /// capability gaps, unusable paths) is mapped onto the typed
    /// <c>DvmConsole</c> result factories; cancellation is reported as a
    /// Cancelled result, never thrown. The provider remains owned by its
    /// TopLevel and stays usable after the service is disposed.
    /// </summary>
    public sealed class AvaloniaFileDialogService : IFileDialogService
    {
        private readonly IStorageProvider _provider;

        /// <summary>
        /// Creates a service bound to the given storage provider.
        /// </summary>
        /// <param name="provider">The provider that shows the native pickers;
        /// normally owned by an Avalonia <see cref="Avalonia.Controls.Window"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
        public AvaloniaFileDialogService(IStorageProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        /// <inheritdoc />
        public async Task<FileDialogResult> OpenFileAsync(
            OpenFileRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Filters);

            if (cancellationToken.IsCancellationRequested)
            {
                return FileDialogResultBase.Cancelled();
            }

            try
            {
                if (!_provider.CanOpen)
                {
                    return FileDialogResultBase.Cancelled();
                }

                var options = new FilePickerOpenOptions
                {
                    Title = request.Title,
                    AllowMultiple = request.AllowMultiple,
                    FileTypeFilter = MapFilters(request.Filters),
                };

                IStorageFolder? startFolder = await ResolveStartFolderAsync(request.InitialDirectory);
                if (startFolder is not null)
                {
                    options.SuggestedStartLocation = startFolder;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return FileDialogResultBase.Cancelled();
                }

                IReadOnlyList<IStorageFile>? results =
                    await _provider.OpenFilePickerAsync(options);

                if (cancellationToken.IsCancellationRequested)
                {
                    return FileDialogResultBase.Cancelled();
                }

                if (results is null || results.Count == 0)
                {
                    return FileDialogResultBase.Cancelled();
                }

                var paths = new List<string>(results.Count);
                foreach (IStorageFile item in results)
                {
                    string? localPath = item.TryGetLocalPath();
                    if (string.IsNullOrEmpty(localPath))
                    {
                        return FileDialogResultBase.Cancelled();
                    }

                    paths.Add(localPath);
                }

                return FileDialogResultBase.FromSelections(paths);
            }
            catch (OperationCanceledException)
            {
                return FileDialogResultBase.Cancelled();
            }
        }

        /// <inheritdoc />
        public async Task<FileDialogResult> SaveFileAsync(
            SaveFileRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Filters);

            if (cancellationToken.IsCancellationRequested)
            {
                return FileDialogResultBase.Cancelled();
            }

            try
            {
                if (!_provider.CanSave)
                {
                    return FileDialogResultBase.Cancelled();
                }

                var options = new FilePickerSaveOptions
                {
                    Title = request.Title,
                    SuggestedFileName = request.DefaultFileName,
                    DefaultExtension = DeriveDefaultExtension(request.Filters),
                    FileTypeChoices = MapFilters(request.Filters),
                };

                IStorageFolder? startFolder = await ResolveStartFolderAsync(request.InitialDirectory);
                if (startFolder is not null)
                {
                    options.SuggestedStartLocation = startFolder;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return FileDialogResultBase.Cancelled();
                }

                IStorageFile? result = await _provider.SaveFilePickerAsync(options);

                if (cancellationToken.IsCancellationRequested)
                {
                    return FileDialogResultBase.Cancelled();
                }

                if (result is null)
                {
                    return FileDialogResultBase.Cancelled();
                }

                string? localPath = result.TryGetLocalPath();
                if (string.IsNullOrEmpty(localPath))
                {
                    return FileDialogResultBase.Cancelled();
                }

                return FileDialogResultBase.FromSelection(localPath);
            }
            catch (OperationCanceledException)
            {
                return FileDialogResultBase.Cancelled();
            }
        }

        /// <inheritdoc />
        public async Task<FolderDialogResult> PickFolderAsync(
            FolderPickerRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (cancellationToken.IsCancellationRequested)
            {
                return FolderDialogResultBase.Cancelled();
            }

            try
            {
                if (!_provider.CanPickFolder)
                {
                    return FolderDialogResultBase.Cancelled();
                }

                var options = new FolderPickerOpenOptions { Title = request.Title };

                IStorageFolder? startFolder = await ResolveStartFolderAsync(request.InitialDirectory);
                if (startFolder is not null)
                {
                    options.SuggestedStartLocation = startFolder;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return FolderDialogResultBase.Cancelled();
                }

                IReadOnlyList<IStorageFolder>? results =
                    await _provider.OpenFolderPickerAsync(options);

                if (cancellationToken.IsCancellationRequested)
                {
                    return FolderDialogResultBase.Cancelled();
                }

                if (results is null || results.Count == 0)
                {
                    return FolderDialogResultBase.Cancelled();
                }

                string? localPath = results[0].TryGetLocalPath();
                if (string.IsNullOrEmpty(localPath))
                {
                    return FolderDialogResultBase.Cancelled();
                }

                return FolderDialogResultBase.FromSelection(localPath);
            }
            catch (OperationCanceledException)
            {
                return FolderDialogResultBase.Cancelled();
            }
        }

        /// <summary>
        /// Disposal is a no-op and idempotent: the provider remains owned by
        /// its TopLevel and usable after the service is disposed.
        /// </summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>
        /// Maps the DvmConsole filters onto Avalonia file types, in order. An
        /// empty filter list maps to Avalonia's <c>All</c> file type rather
        /// than an empty (and therefore useless) filter collection.
        /// </summary>
        private static IReadOnlyList<FilePickerFileType> MapFilters(
            IReadOnlyList<FileDialogFilter> filters)
        {
            if (filters.Count == 0)
            {
                return new[] { FilePickerFileTypes.All };
            }

            var types = new List<FilePickerFileType>(filters.Count);
            foreach (FileDialogFilter filter in filters)
            {
                types.Add(new FilePickerFileType(filter.Name)
                {
                    Patterns = filter.Patterns.ToArray(),
                });
            }

            return types;
        }

        /// <summary>
        /// Derives the Avalonia default extension from the first pattern of
        /// the first filter, stripping whitespace and any leading glob dot or
        /// path separator ("*.wav" becomes "wav"); null when unavailable.
        /// </summary>
        private static string? DeriveDefaultExtension(IReadOnlyList<FileDialogFilter> filters)
        {
            if (filters.Count == 0 || filters[0].Patterns.Count == 0)
            {
                return null;
            }

            string extension = filters[0].Patterns[0]
                .Trim()
                .TrimStart('*', '.', '/', '\\');

            return string.IsNullOrEmpty(extension) ? null : extension;
        }

        /// <summary>
        /// Best-effort resolution of the requested initial directory through
        /// the provider: a null or whitespace directory is skipped entirely, a
        /// null result or any non-cancellation exception means the dialog
        /// proceeds without a start location. Provider cancellation is not
        /// swallowed here; it propagates as a typed Cancelled outcome.
        /// </summary>
        private async Task<IStorageFolder?> ResolveStartFolderAsync(string? initialDirectory)
        {
            if (string.IsNullOrWhiteSpace(initialDirectory))
            {
                return null;
            }

            try
            {
                if (!Uri.TryCreate(initialDirectory, UriKind.Absolute, out Uri? uri) || uri is null)
                {
                    uri = new Uri("file://" + Path.GetFullPath(initialDirectory));
                }

                return await _provider.TryGetFolderFromPathAsync(uri);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }
    }
}
