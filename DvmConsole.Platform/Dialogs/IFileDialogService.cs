// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Dialogs
{
    /// <summary>
    /// A named file filter with one or more patterns (e.g. "WAV files" / "*.wav").
    /// </summary>
    public sealed class FileDialogFilter
    {
        /// <summary>
        /// Creates a named filter.
        /// </summary>
        /// <param name="name">Human-readable filter name shown in the dialog.</param>
        /// <param name="patterns">Glob patterns, e.g. "*.wav".</param>
        public FileDialogFilter(string name, IReadOnlyList<string> patterns)
        {
            Name = name;
            Patterns = patterns;
        }

        /// <summary>Human-readable filter name shown in the dialog.</summary>
        public string Name { get; }

        /// <summary>Glob patterns, e.g. "*.wav".</summary>
        public IReadOnlyList<string> Patterns { get; }
    }

    /// <summary>
    /// Request describing an open-file dialog.
    /// </summary>
    /// <param name="Title">Dialog title, or null for the platform default.</param>
    /// <param name="Filters">Allowed file filters; required (null is rejected by the service).</param>
    /// <param name="AllowMultiple">When true, the dialog allows selecting several files.</param>
    /// <param name="InitialDirectory">Directory the dialog opens in, or null for the default.</param>
    public sealed record OpenFileRequest(
        string? Title,
        IReadOnlyList<FileDialogFilter> Filters,
        bool AllowMultiple,
        string? InitialDirectory);

    /// <summary>
    /// Request describing a save-file dialog.
    /// </summary>
    /// <param name="Title">Dialog title, or null for the platform default.</param>
    /// <param name="Filters">Allowed file filters; required (null is rejected by the service).</param>
    /// <param name="DefaultFileName">File name pre-filled in the dialog, or null.</param>
    /// <param name="InitialDirectory">Directory the dialog opens in, or null for the default.</param>
    public sealed record SaveFileRequest(
        string? Title,
        IReadOnlyList<FileDialogFilter> Filters,
        string? DefaultFileName,
        string? InitialDirectory);

    /// <summary>
    /// Request describing a folder-picker dialog.
    /// </summary>
    /// <param name="Title">Dialog title, or null for the platform default.</param>
    /// <param name="InitialDirectory">Directory the dialog opens in, or null for the default.</param>
    public sealed record FolderPickerRequest(string? Title, string? InitialDirectory);

    /// <summary>
    /// Static factories for <see cref="FileDialogResult"/>. The factories live on
    /// this base type because C# forbids a static factory and an instance property
    /// sharing the name <c>Cancelled</c> inside a single type.
    /// </summary>
    public abstract class FileDialogResultBase
    {
        /// <summary>A result for a dismissed dialog.</summary>
        public static FileDialogResult Cancelled() => new FileDialogResult(null, Array.Empty<string>(), true);

        /// <summary>A result carrying a single selected path.</summary>
        public static FileDialogResult FromSelection(string path) => new FileDialogResult(path, new[] { path }, false);

        /// <summary>A result carrying several selected paths; Selected is the first.</summary>
        public static FileDialogResult FromSelections(IReadOnlyList<string> paths)
            => new FileDialogResult(paths.Count > 0 ? paths[0] : null, paths, false);
    }

    /// <summary>
    /// Result of an open or save file dialog.
    /// </summary>
    public sealed class FileDialogResult : FileDialogResultBase
    {
        internal FileDialogResult(string? selected, IReadOnlyList<string> selectedMany, bool cancelled)
        {
            Selected = selected;
            SelectedMany = selectedMany;
            Cancelled = cancelled;
        }

        /// <summary>The selected file path, the first of several, or null when cancelled.</summary>
        public string? Selected { get; }

        /// <summary>All selected file paths (a single-element list for a single selection,
        /// empty when cancelled).</summary>
        public IReadOnlyList<string> SelectedMany { get; }

        /// <summary>True when the dialog was dismissed without a selection.</summary>
        public new bool Cancelled { get; }
    }

    /// <summary>
    /// Static factories for <see cref="FolderDialogResult"/>. The factories live on
    /// this base type because C# forbids a static factory and an instance property
    /// sharing the name <c>Cancelled</c> inside a single type.
    /// </summary>
    public abstract class FolderDialogResultBase
    {
        /// <summary>A result for a dismissed dialog.</summary>
        public static FolderDialogResult Cancelled() => new FolderDialogResult(null, true);

        /// <summary>A result carrying the picked folder.</summary>
        public static FolderDialogResult FromSelection(string path) => new FolderDialogResult(path, false);
    }

    /// <summary>
    /// Result of a folder-picker dialog.
    /// </summary>
    public sealed class FolderDialogResult : FolderDialogResultBase
    {
        internal FolderDialogResult(string? selected, bool cancelled)
        {
            Selected = selected;
            Cancelled = cancelled;
        }

        /// <summary>The picked folder path, or null when cancelled.</summary>
        public string? Selected { get; }

        /// <summary>True when the dialog was dismissed without a selection.</summary>
        public new bool Cancelled { get; }
    }

    /// <summary>
    /// File and folder pickers for the host desktop shell. Cancellation (by the
    /// user or via the token) is reported as a Cancelled result, never thrown.
    /// </summary>
    public interface IFileDialogService : IAsyncDisposable
    {
        /// <summary>Shows an open-file dialog.</summary>
        Task<FileDialogResult> OpenFileAsync(OpenFileRequest request, CancellationToken cancellationToken);

        /// <summary>Shows a save-file dialog.</summary>
        Task<FileDialogResult> SaveFileAsync(SaveFileRequest request, CancellationToken cancellationToken);

        /// <summary>Shows a folder-picker dialog.</summary>
        Task<FolderDialogResult> PickFolderAsync(FolderPickerRequest request, CancellationToken cancellationToken);
    }
}
