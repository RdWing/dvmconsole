// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Dialogs;

namespace DvmConsole.Avalonia.Dialogs
{
    /// <summary>
    /// Fallback <see cref="IFileDialogService"/> used by the shell until a
    /// storage-provider-backed service is injected. Every dialog is reported
    /// as Cancelled, so shell behavior is unchanged until features consume
    /// the service.
    /// </summary>
    public sealed class NoopFileDialogService : IFileDialogService
    {
        /// <summary>The shared no-op instance.</summary>
        public static NoopFileDialogService Instance { get; } = new NoopFileDialogService();

        private NoopFileDialogService()
        {
        }

        /// <inheritdoc />
        public Task<FileDialogResult> OpenFileAsync(
            OpenFileRequest request, CancellationToken cancellationToken)
            => Task.FromResult(FileDialogResultBase.Cancelled());

        /// <inheritdoc />
        public Task<FileDialogResult> SaveFileAsync(
            SaveFileRequest request, CancellationToken cancellationToken)
            => Task.FromResult(FileDialogResultBase.Cancelled());

        /// <inheritdoc />
        public Task<FolderDialogResult> PickFolderAsync(
            FolderPickerRequest request, CancellationToken cancellationToken)
            => Task.FromResult(FolderDialogResultBase.Cancelled());

        /// <summary>
        /// Disposal is a no-op and idempotent; the singleton stays usable.
        /// </summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
