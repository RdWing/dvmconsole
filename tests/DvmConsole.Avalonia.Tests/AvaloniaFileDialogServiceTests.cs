// SPDX-License-Identifier: AGPL-3.0-only
/**
* Dedicated contract gate for the DvmConsole.Avalonia.Dialogs.AvaloniaFileDialogService
* adapter (the IFileDialogService implementation for the Avalonia shell). These
* facts are written entirely against the agreed contract: the service takes an
* Avalonia.Platform.Storage.IStorageProvider in its constructor, maps every
* dialog outcome (selection, user cancel, provider cancellation, capability
* gaps, unusable paths) onto the typed DvmConsole result factories, and never
* touches files, displays, secrets, or native pickers directly.
*
* The tests are fully headless and deterministic: Avalonia's
* [NotClientImplementable] IStorageProvider/IStorageFile/IStorageFolder are
* faked with DispatchProxy, so no Avalonia.Headless package, display, real
* filesystem, or native call is involved.
*
* This project exercises the implemented AvaloniaFileDialogService contract.
*/
#nullable enable
using System.Reflection;
using Avalonia.Platform.Storage;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Platform.Dialogs;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Contract gate for <c>AvaloniaFileDialogService</c> against the
    /// <see cref="IFileDialogService"/> interface.
    /// </summary>
    public sealed class AvaloniaFileDialogServiceTests
    {
        /// <summary>Which dialog operation a theory exercises.</summary>
        public enum DialogKind
        {
            Open,
            Save,
            Folder,
        }

        private static readonly IReadOnlyList<FileDialogFilter> DefaultFilters =
            new[] { new FileDialogFilter("All files", new[] { "*" }) };

        private const string SelectedFilePath = "/tmp/dvmconsole/qso.wav";
        private const string InitialDirectoryPath = "/tmp/dvmconsole";

        // ---- Factories -------------------------------------------------------

        private static OpenFileRequest OpenRequest(
            string? title = null,
            IReadOnlyList<FileDialogFilter>? filters = null,
            bool allowMultiple = false,
            string? initialDirectory = null)
            => new(title, filters ?? DefaultFilters, allowMultiple, initialDirectory);

        private static SaveFileRequest SaveRequest(
            string? title = null,
            IReadOnlyList<FileDialogFilter>? filters = null,
            string? defaultFileName = null,
            string? initialDirectory = null)
            => new(title, filters ?? DefaultFilters, defaultFileName, initialDirectory);

        private static FolderPickerRequest FolderRequest(
            string? title = null,
            string? initialDirectory = null)
            => new(title, initialDirectory);

        private static StorageProviderProxy CreateProviderProxy(
            bool canOpen = true,
            bool canSave = true,
            bool canPickFolder = true)
        {
            var provider = DispatchProxy.Create<IStorageProvider, StorageProviderProxy>();
            var proxy = (StorageProviderProxy)(object)provider;
            proxy.CanOpen = canOpen;
            proxy.CanSave = canSave;
            proxy.CanPickFolder = canPickFolder;
            proxy.OpenFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>(Array.Empty<IStorageFile>());
            proxy.SaveFilePicker = _ => Task.FromResult<IStorageFile>(null!);
            proxy.OpenFolderPicker = _ => Task.FromResult<IReadOnlyList<IStorageFolder>>(Array.Empty<IStorageFolder>());
            proxy.TryGetFolderFromPath = _ => Task.FromResult<IStorageFolder>(null!);
            return proxy;
        }

        private static AvaloniaFileDialogService CreateService(StorageProviderProxy providerProxy)
            => new((IStorageProvider)providerProxy);

        private static IStorageFile StorageFile(string localPath)
            => StorageFile(new Uri("file://" + localPath));

        private static IStorageFile StorageFile(Uri path)
        {
            var file = DispatchProxy.Create<IStorageFile, StorageItemProxy>();
            var proxy = (StorageItemProxy)(object)file;
            proxy.Path = path;
            return file;
        }

        private static IStorageFolder StorageFolder(string localPath)
            => StorageFolder(new Uri("file://" + localPath));

        private static IStorageFolder StorageFolder(Uri path)
        {
            var folder = DispatchProxy.Create<IStorageFolder, StorageItemProxy>();
            var proxy = (StorageItemProxy)(object)folder;
            proxy.Path = path;
            return folder;
        }

        private static string PickerMethod(DialogKind kind) => kind switch
        {
            DialogKind.Open => "OpenFilePickerAsync",
            DialogKind.Save => "SaveFilePickerAsync",
            DialogKind.Folder => "OpenFolderPickerAsync",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        private static async Task<object> RunAsync(
            AvaloniaFileDialogService service, DialogKind kind, CancellationToken token)
            => kind switch
            {
                DialogKind.Open => await service.OpenFileAsync(OpenRequest(), token),
                DialogKind.Save => await service.SaveFileAsync(SaveRequest(), token),
                DialogKind.Folder => await service.PickFolderAsync(FolderRequest(), token),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

        private static void AssertTypedCancelled(object outcome)
        {
            switch (outcome)
            {
                case FileDialogResult file:
                    Assert.True(file.Cancelled);
                    Assert.Null(file.Selected);
                    Assert.Empty(file.SelectedMany);
                    break;
                case FolderDialogResult folder:
                    Assert.True(folder.Cancelled);
                    Assert.Null(folder.Selected);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected dialog result type {outcome.GetType().Name}.");
            }
        }

        // ---- Null arguments --------------------------------------------------

        /// <summary>
        /// A null open request is a programming error, not a dialog outcome.
        /// </summary>
        [Fact]
        public async Task NullRequest_Open_ThrowsArgumentNullException()
        {
            await using var service = CreateService(CreateProviderProxy());

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => service.OpenFileAsync(null!, CancellationToken.None));
        }

        /// <summary>
        /// A null filter list is a programming error, not a dialog outcome.
        /// </summary>
        [Fact]
        public async Task NullFilters_Open_ThrowsArgumentNullException()
        {
            await using var service = CreateService(CreateProviderProxy());

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => service.OpenFileAsync(
                    new OpenFileRequest("Open", null!, false, null), CancellationToken.None));
        }

        /// <summary>
        /// A null save request is a programming error, not a dialog outcome.
        /// </summary>
        [Fact]
        public async Task NullRequest_Save_ThrowsArgumentNullException()
        {
            await using var service = CreateService(CreateProviderProxy());

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => service.SaveFileAsync(null!, CancellationToken.None));
        }

        /// <summary>
        /// A null filter list is a programming error, not a dialog outcome.
        /// </summary>
        [Fact]
        public async Task NullFilters_Save_ThrowsArgumentNullException()
        {
            await using var service = CreateService(CreateProviderProxy());

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => service.SaveFileAsync(
                    new SaveFileRequest("Save", null!, null, null), CancellationToken.None));
        }

        /// <summary>
        /// A null folder request is a programming error, not a dialog outcome.
        /// </summary>
        [Fact]
        public async Task NullRequest_PickFolder_ThrowsArgumentNullException()
        {
            await using var service = CreateService(CreateProviderProxy());

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => service.PickFolderAsync(null!, CancellationToken.None));
        }

        // ---- Pre-cancelled tokens ---------------------------------------------

        /// <summary>
        /// A token that is already cancelled must short-circuit to a typed
        /// Cancelled result without touching the provider at all.
        /// </summary>
        [Theory]
        [InlineData(DialogKind.Open)]
        [InlineData(DialogKind.Save)]
        [InlineData(DialogKind.Folder)]
        public async Task PreCancelledToken_DoesNotInvokeProvider_ReturnsCancelled(DialogKind kind)
        {
            var proxy = CreateProviderProxy();
            await using var service = CreateService(proxy);
            var cancelled = new CancellationToken(canceled: true);

            var outcome = await RunAsync(service, kind, cancelled);

            AssertTypedCancelled(outcome);
            Assert.DoesNotContain(PickerMethod(kind), proxy.Calls);
        }

        // ---- Cancellation from the provider -----------------------------------

        /// <summary>
        /// When the platform signals cancellation by throwing
        /// OperationCanceledException, the service must report a typed Cancelled
        /// result and never propagate the exception.
        /// </summary>
        [Theory]
        [InlineData(DialogKind.Open)]
        [InlineData(DialogKind.Save)]
        [InlineData(DialogKind.Folder)]
        public async Task ProviderThrowsOperationCanceled_ReturnsCancelled(DialogKind kind)
        {
            var proxy = CreateProviderProxy();
            switch (kind)
            {
                case DialogKind.Open:
                    proxy.OpenFilePicker = _ => throw new OperationCanceledException();
                    break;
                case DialogKind.Save:
                    proxy.SaveFilePicker = _ => throw new OperationCanceledException();
                    break;
                case DialogKind.Folder:
                    proxy.OpenFolderPicker = _ => throw new OperationCanceledException();
                    break;
            }
            await using var service = CreateService(proxy);

            var outcome = await RunAsync(service, kind, CancellationToken.None);

            AssertTypedCancelled(outcome);
        }

        /// <summary>
        /// If the caller cancels while the native dialog is showing, the token
        /// is cancelled by the time the provider completes; the completed
        /// selection must still map to a typed Cancelled result.
        /// </summary>
        [Theory]
        [InlineData(DialogKind.Open)]
        [InlineData(DialogKind.Save)]
        [InlineData(DialogKind.Folder)]
        public async Task TokenCancelledAfterProviderCompletion_ReturnsCancelled(DialogKind kind)
        {
            var proxy = CreateProviderProxy();
            var cts = new CancellationTokenSource();
            switch (kind)
            {
                case DialogKind.Open:
                    proxy.OpenFilePicker = _ =>
                    {
                        cts.Cancel();
                        return Task.FromResult<IReadOnlyList<IStorageFile>>(
                            new[] { StorageFile(SelectedFilePath) });
                    };
                    break;
                case DialogKind.Save:
                    proxy.SaveFilePicker = _ =>
                    {
                        cts.Cancel();
                        return Task.FromResult<IStorageFile>(StorageFile(SelectedFilePath));
                    };
                    break;
                case DialogKind.Folder:
                    proxy.OpenFolderPicker = _ =>
                    {
                        cts.Cancel();
                        return Task.FromResult<IReadOnlyList<IStorageFolder>>(
                            new[] { StorageFolder(InitialDirectoryPath) });
                    };
                    break;
            }
            await using var service = CreateService(proxy);

            var outcome = await RunAsync(service, kind, cts.Token);

            AssertTypedCancelled(outcome);
        }

        // ---- Capability gaps ---------------------------------------------------

        /// <summary>
        /// A provider that cannot show a picker must yield a typed Cancelled
        /// result without the picker being invoked.
        /// </summary>
        [Theory]
        [InlineData(DialogKind.Open)]
        [InlineData(DialogKind.Save)]
        [InlineData(DialogKind.Folder)]
        public async Task ProviderCannotShowDialog_ReturnsCancelled_WithoutInvokingPicker(DialogKind kind)
        {
            var proxy = kind switch
            {
                DialogKind.Open => CreateProviderProxy(canOpen: false),
                DialogKind.Save => CreateProviderProxy(canSave: false),
                DialogKind.Folder => CreateProviderProxy(canPickFolder: false),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            await using var service = CreateService(proxy);

            var outcome = await RunAsync(service, kind, CancellationToken.None);

            AssertTypedCancelled(outcome);
            Assert.DoesNotContain(PickerMethod(kind), proxy.Calls);
        }

        // ---- User cancel (empty or null provider results) ----------------------

        /// <summary>
        /// An empty open result is the provider's user-cancel signal and maps
        /// to the typed Cancelled result with no selections.
        /// </summary>
        [Fact]
        public async Task Open_EmptyResult_ReturnsCancelled()
        {
            var proxy = CreateProviderProxy();
            proxy.OpenFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>(Array.Empty<IStorageFile>());
            await using var service = CreateService(proxy);

            var result = await service.OpenFileAsync(OpenRequest(), CancellationToken.None);

            AssertTypedCancelled(result);
        }

        /// <summary>
        /// A null save result is the provider's user-cancel signal and maps to
        /// the typed Cancelled result.
        /// </summary>
        [Fact]
        public async Task Save_NullResult_ReturnsCancelled()
        {
            var proxy = CreateProviderProxy();
            proxy.SaveFilePicker = _ => Task.FromResult<IStorageFile>(null!);
            await using var service = CreateService(proxy);

            var result = await service.SaveFileAsync(SaveRequest(), CancellationToken.None);

            AssertTypedCancelled(result);
        }

        /// <summary>
        /// An empty folder result is the provider's user-cancel signal and maps
        /// to the typed Cancelled result.
        /// </summary>
        [Fact]
        public async Task PickFolder_EmptyResult_ReturnsCancelled()
        {
            var proxy = CreateProviderProxy();
            proxy.OpenFolderPicker = _ => Task.FromResult<IReadOnlyList<IStorageFolder>>(Array.Empty<IStorageFolder>());
            await using var service = CreateService(proxy);

            var result = await service.PickFolderAsync(FolderRequest(), CancellationToken.None);

            AssertTypedCancelled(result);
        }

        // ---- Selection mapping ---------------------------------------------------

        /// <summary>
        /// A single open selection maps its local path into Selected and
        /// SelectedMany (single-element), exactly like FromSelection.
        /// </summary>
        [Fact]
        public async Task Open_SingleFile_MapsLocalPathToSelected()
        {
            var proxy = CreateProviderProxy();
            proxy.OpenFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>(
                new[] { StorageFile(SelectedFilePath) });
            await using var service = CreateService(proxy);

            var result = await service.OpenFileAsync(OpenRequest(), CancellationToken.None);

            Assert.False(result.Cancelled);
            Assert.Equal(SelectedFilePath, result.Selected);
            Assert.Equal(new[] { SelectedFilePath }, result.SelectedMany);
        }

        /// <summary>
        /// A multi-open selection maps every local path into SelectedMany with
        /// the first path in Selected, exactly like FromSelections.
        /// </summary>
        [Fact]
        public async Task Open_MultipleFiles_MapsAllLocalPathsToSelectedMany()
        {
            const string secondPath = "/tmp/dvmconsole/qso2.wav";
            var proxy = CreateProviderProxy();
            proxy.OpenFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>(
                new[] { StorageFile(SelectedFilePath), StorageFile(secondPath) });
            await using var service = CreateService(proxy);

            var result = await service.OpenFileAsync(
                OpenRequest(allowMultiple: true), CancellationToken.None);

            Assert.False(result.Cancelled);
            Assert.Equal(SelectedFilePath, result.Selected);
            Assert.Equal(new[] { SelectedFilePath, secondPath }, result.SelectedMany);
        }

        /// <summary>
        /// A save selection maps its local path into Selected and a
        /// single-element SelectedMany.
        /// </summary>
        [Fact]
        public async Task Save_MapsLocalPathToSelected()
        {
            var proxy = CreateProviderProxy();
            proxy.SaveFilePicker = _ => Task.FromResult<IStorageFile>(StorageFile(SelectedFilePath));
            await using var service = CreateService(proxy);

            var result = await service.SaveFileAsync(SaveRequest(), CancellationToken.None);

            Assert.False(result.Cancelled);
            Assert.Equal(SelectedFilePath, result.Selected);
            Assert.Equal(new[] { SelectedFilePath }, result.SelectedMany);
        }

        /// <summary>
        /// A folder selection maps its local path into Selected.
        /// </summary>
        [Fact]
        public async Task PickFolder_MapsLocalPathToSelected()
        {
            var proxy = CreateProviderProxy();
            proxy.OpenFolderPicker = _ => Task.FromResult<IReadOnlyList<IStorageFolder>>(
                new[] { StorageFolder(InitialDirectoryPath) });
            await using var service = CreateService(proxy);

            var result = await service.PickFolderAsync(FolderRequest(), CancellationToken.None);

            Assert.False(result.Cancelled);
            Assert.Equal(InitialDirectoryPath, result.Selected);
        }

        // ---- Unusable paths ------------------------------------------------------

        /// <summary>
        /// A selected item whose URI has no local path (non-file scheme) cannot
        /// be reported as a selection and maps to the typed Cancelled result.
        /// </summary>
        [Theory]
        [InlineData(DialogKind.Open)]
        [InlineData(DialogKind.Save)]
        [InlineData(DialogKind.Folder)]
        public async Task NonFileUriSelection_ReturnsCancelled(DialogKind kind)
        {
            var proxy = CreateProviderProxy();
            var remote = new Uri("http://example.com/qso.wav");
            switch (kind)
            {
                case DialogKind.Open:
                    proxy.OpenFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>(
                        new[] { StorageFile(remote) });
                    break;
                case DialogKind.Save:
                    proxy.SaveFilePicker = _ => Task.FromResult<IStorageFile>(StorageFile(remote));
                    break;
                case DialogKind.Folder:
                    proxy.OpenFolderPicker = _ => Task.FromResult<IReadOnlyList<IStorageFolder>>(
                        new[] { StorageFolder(remote) });
                    break;
            }
            await using var service = CreateService(proxy);

            var outcome = await RunAsync(service, kind, CancellationToken.None);

            AssertTypedCancelled(outcome);
            Assert.Contains(PickerMethod(kind), proxy.Calls);
        }

        // ---- Filter mapping --------------------------------------------------------

        /// <summary>
        /// Open filters map name and patterns onto Avalonia FilePickerFileType
        /// entries in order.
        /// </summary>
        [Fact]
        public async Task Open_Filters_MapNamesAndPatternsToFileTypeFilter()
        {
            var proxy = CreateProviderProxy();
            FilePickerOpenOptions? captured = null;
            proxy.OpenFilePicker = args =>
            {
                captured = args?[0] as FilePickerOpenOptions;
                return Task.FromResult<IReadOnlyList<IStorageFile>>(Array.Empty<IStorageFile>());
            };
            await using var service = CreateService(proxy);
            var filters = new[]
            {
                new FileDialogFilter("WAV files", new[] { "*.wav" }),
                new FileDialogFilter("All files", new[] { "*" }),
            };

            await service.OpenFileAsync(OpenRequest(filters: filters), CancellationToken.None);

            Assert.NotNull(captured);
            var mapped = captured!.FileTypeFilter;
            Assert.NotNull(mapped);
            Assert.Collection(
                mapped!,
                ft =>
                {
                    Assert.Equal("WAV files", ft.Name);
                    Assert.Equal(new[] { "*.wav" }, ft.Patterns);
                },
                ft =>
                {
                    Assert.Equal("All files", ft.Name);
                    Assert.Equal(new[] { "*" }, ft.Patterns);
                });
        }

        /// <summary>
        /// Save filters map name and patterns onto Avalonia FilePickerFileType
        /// entries in order.
        /// </summary>
        [Fact]
        public async Task Save_Filters_MapNamesAndPatternsToFileTypeChoices()
        {
            var proxy = CreateProviderProxy();
            FilePickerSaveOptions? captured = null;
            proxy.SaveFilePicker = args =>
            {
                captured = args?[0] as FilePickerSaveOptions;
                return Task.FromResult<IStorageFile>(null!);
            };
            await using var service = CreateService(proxy);
            var filters = new[]
            {
                new FileDialogFilter("WAV files", new[] { "*.wav" }),
                new FileDialogFilter("All files", new[] { "*" }),
            };

            await service.SaveFileAsync(SaveRequest(filters: filters), CancellationToken.None);

            Assert.NotNull(captured);
            var mapped = captured!.FileTypeChoices;
            Assert.NotNull(mapped);
            Assert.Collection(
                mapped!,
                ft =>
                {
                    Assert.Equal("WAV files", ft.Name);
                    Assert.Equal(new[] { "*.wav" }, ft.Patterns);
                },
                ft =>
                {
                    Assert.Equal("All files", ft.Name);
                    Assert.Equal(new[] { "*" }, ft.Patterns);
                });
        }

        /// <summary>
        /// An empty open filter list maps to Avalonia's All file type rather
        /// than an empty (and therefore useless) filter collection.
        /// </summary>
        [Fact]
        public async Task Open_EmptyFilters_MapsToAvaloniaAll()
        {
            var proxy = CreateProviderProxy();
            FilePickerOpenOptions? captured = null;
            proxy.OpenFilePicker = args =>
            {
                captured = args?[0] as FilePickerOpenOptions;
                return Task.FromResult<IReadOnlyList<IStorageFile>>(Array.Empty<IStorageFile>());
            };
            await using var service = CreateService(proxy);

            await service.OpenFileAsync(
                OpenRequest(filters: Array.Empty<FileDialogFilter>()), CancellationToken.None);

            Assert.NotNull(captured);
            var mapped = captured!.FileTypeFilter;
            Assert.NotNull(mapped);
            Assert.Same(FilePickerFileTypes.All, Assert.Single(mapped!));
        }

        /// <summary>
        /// An empty save filter list maps to Avalonia's All file type rather
        /// than an empty (and therefore useless) filter collection.
        /// </summary>
        [Fact]
        public async Task Save_EmptyFilters_MapsToAvaloniaAll()
        {
            var proxy = CreateProviderProxy();
            FilePickerSaveOptions? captured = null;
            proxy.SaveFilePicker = args =>
            {
                captured = args?[0] as FilePickerSaveOptions;
                return Task.FromResult<IStorageFile>(null!);
            };
            await using var service = CreateService(proxy);

            await service.SaveFileAsync(
                SaveRequest(filters: Array.Empty<FileDialogFilter>()), CancellationToken.None);

            Assert.NotNull(captured);
            var mapped = captured!.FileTypeChoices;
            Assert.NotNull(mapped);
            Assert.Same(FilePickerFileTypes.All, Assert.Single(mapped!));
        }

        // ---- Options mapping --------------------------------------------------------

        /// <summary>
        /// Open options carry the requested title and multi-select flag.
        /// </summary>
        [Fact]
        public async Task Open_TitleAndAllowMultiple_MapToOptions()
        {
            var proxy = CreateProviderProxy();
            FilePickerOpenOptions? captured = null;
            proxy.OpenFilePicker = args =>
            {
                captured = args?[0] as FilePickerOpenOptions;
                return Task.FromResult<IReadOnlyList<IStorageFile>>(Array.Empty<IStorageFile>());
            };
            await using var service = CreateService(proxy);

            await service.OpenFileAsync(
                OpenRequest(title: "Open recording", allowMultiple: true), CancellationToken.None);

            Assert.NotNull(captured);
            Assert.Equal("Open recording", captured!.Title);
            Assert.True(captured.AllowMultiple);
        }

        /// <summary>
        /// Save options carry the requested title and suggested file name.
        /// </summary>
        [Fact]
        public async Task Save_TitleAndDefaultFileName_MapToOptions()
        {
            var proxy = CreateProviderProxy();
            FilePickerSaveOptions? captured = null;
            proxy.SaveFilePicker = args =>
            {
                captured = args?[0] as FilePickerSaveOptions;
                return Task.FromResult<IStorageFile>(null!);
            };
            await using var service = CreateService(proxy);

            await service.SaveFileAsync(
                SaveRequest(title: "Save recording", defaultFileName: "qso.wav"), CancellationToken.None);

            Assert.NotNull(captured);
            Assert.Equal("Save recording", captured!.Title);
            Assert.Equal("qso.wav", captured.SuggestedFileName);
        }

        /// <summary>
        /// The Avalonia default extension is derived from the first pattern of
        /// the first filter, with the glob prefix stripped: "*.wav" becomes
        /// "wav".
        /// </summary>
        [Fact]
        public async Task Save_DefaultExtension_DerivedFromFirstPattern()
        {
            var proxy = CreateProviderProxy();
            FilePickerSaveOptions? captured = null;
            proxy.SaveFilePicker = args =>
            {
                captured = args?[0] as FilePickerSaveOptions;
                return Task.FromResult<IStorageFile>(null!);
            };
            await using var service = CreateService(proxy);
            var filters = new[] { new FileDialogFilter("WAV files", new[] { "*.wav" }) };

            await service.SaveFileAsync(SaveRequest(filters: filters), CancellationToken.None);

            Assert.NotNull(captured);
            Assert.Equal("wav", captured!.DefaultExtension);
        }

        /// <summary>
        /// Folder options carry the requested title.
        /// </summary>
        [Fact]
        public async Task PickFolder_Title_MapsToOptions()
        {
            var proxy = CreateProviderProxy();
            FolderPickerOpenOptions? captured = null;
            proxy.OpenFolderPicker = args =>
            {
                captured = args?[0] as FolderPickerOpenOptions;
                return Task.FromResult<IReadOnlyList<IStorageFolder>>(Array.Empty<IStorageFolder>());
            };
            await using var service = CreateService(proxy);

            await service.PickFolderAsync(
                FolderRequest(title: "Pick recording folder"), CancellationToken.None);

            Assert.NotNull(captured);
            Assert.Equal("Pick recording folder", captured!.Title);
        }

        // ---- Initial directory -------------------------------------------------------

        /// <summary>
        /// A non-empty initial directory is resolved through
        /// TryGetFolderFromPathAsync and the returned folder becomes the
        /// SuggestedStartLocation; the dialog still proceeds.
        /// </summary>
        [Fact]
        public async Task Open_InitialDirectory_ResolvedViaTryGetFolderFromPath()
        {
            var proxy = CreateProviderProxy();
            Uri? capturedPath = null;
            var folder = StorageFolder(InitialDirectoryPath);
            proxy.TryGetFolderFromPath = args =>
            {
                capturedPath = args?[0] as Uri;
                return Task.FromResult<IStorageFolder>(folder);
            };
            FilePickerOpenOptions? captured = null;
            proxy.OpenFilePicker = args =>
            {
                captured = args?[0] as FilePickerOpenOptions;
                return Task.FromResult<IReadOnlyList<IStorageFile>>(
                    new[] { StorageFile(SelectedFilePath) });
            };
            await using var service = CreateService(proxy);

            var result = await service.OpenFileAsync(
                OpenRequest(initialDirectory: InitialDirectoryPath), CancellationToken.None);

            Assert.NotNull(capturedPath);
            Assert.Contains(InitialDirectoryPath, capturedPath!.ToString(), StringComparison.Ordinal);
            Assert.Same(folder, captured!.SuggestedStartLocation);
            Assert.False(result.Cancelled);
            Assert.Equal(SelectedFilePath, result.Selected);
        }

        /// <summary>
        /// A null or whitespace initial directory must fall back to the
        /// platform default and never invoke TryGetFolderFromPathAsync.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Open_InitialDirectory_NullOrWhitespace_SkipsProviderLookup(string? initialDirectory)
        {
            var proxy = CreateProviderProxy();
            proxy.OpenFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>(
                new[] { StorageFile(SelectedFilePath) });
            await using var service = CreateService(proxy);

            var result = await service.OpenFileAsync(
                OpenRequest(initialDirectory: initialDirectory), CancellationToken.None);

            Assert.False(result.Cancelled);
            Assert.Equal(SelectedFilePath, result.Selected);
            Assert.DoesNotContain("TryGetFolderFromPathAsync", proxy.Calls);
        }

        /// <summary>
        /// When TryGetFolderFromPathAsync cannot resolve the directory it
        /// returns null; the dialog proceeds without a start location.
        /// </summary>
        [Fact]
        public async Task Open_InitialDirectory_LookupReturnsNull_ProceedsWithoutStartLocation()
        {
            var proxy = CreateProviderProxy();
            FilePickerOpenOptions? captured = null;
            proxy.OpenFilePicker = args =>
            {
                captured = args?[0] as FilePickerOpenOptions;
                return Task.FromResult<IReadOnlyList<IStorageFile>>(
                    new[] { StorageFile(SelectedFilePath) });
            };
            await using var service = CreateService(proxy);

            var result = await service.OpenFileAsync(
                OpenRequest(initialDirectory: InitialDirectoryPath), CancellationToken.None);

            Assert.NotNull(captured);
            Assert.Null(captured!.SuggestedStartLocation);
            Assert.False(result.Cancelled);
            Assert.Equal(SelectedFilePath, result.Selected);
        }

        /// <summary>
        /// The initial-directory lookup is best effort: when the provider
        /// throws (for example on a permission-denied path) the dialog must
        /// still proceed without a start location.
        /// </summary>
        [Fact]
        public async Task Open_InitialDirectory_LookupThrows_ProceedsBestEffort()
        {
            var proxy = CreateProviderProxy();
            proxy.TryGetFolderFromPath = _ => throw new UnauthorizedAccessException("permission denied");
            FilePickerOpenOptions? captured = null;
            proxy.OpenFilePicker = args =>
            {
                captured = args?[0] as FilePickerOpenOptions;
                return Task.FromResult<IReadOnlyList<IStorageFile>>(
                    new[] { StorageFile(SelectedFilePath) });
            };
            await using var service = CreateService(proxy);

            var result = await service.OpenFileAsync(
                OpenRequest(initialDirectory: InitialDirectoryPath), CancellationToken.None);

            Assert.NotNull(captured);
            Assert.Null(captured!.SuggestedStartLocation);
            Assert.False(result.Cancelled);
            Assert.Equal(SelectedFilePath, result.Selected);
        }

        // ---- Disposal ----------------------------------------------------------------

        /// <summary>
        /// Disposal is idempotent and never disposes the injected provider:
        /// disposing twice must not stop the service from showing dialogs.
        /// </summary>
        [Fact]
        public async Task DisposeTwice_IsIdempotent_AndProviderRemainsUsable()
        {
            var proxy = CreateProviderProxy();
            proxy.OpenFilePicker = _ => Task.FromResult<IReadOnlyList<IStorageFile>>(
                new[] { StorageFile(SelectedFilePath) });
            var service = CreateService(proxy);

            await service.DisposeAsync();
            await service.DisposeAsync();

            var result = await service.OpenFileAsync(OpenRequest(), CancellationToken.None);

            Assert.False(result.Cancelled);
            Assert.Equal(SelectedFilePath, result.Selected);
        }

        // ---- Contract shape -------------------------------------------------------------

        /// <summary>
        /// Compile-time gates: the constructor must accept IStorageProvider and
        /// the type must implement IFileDialogService (and thus IAsyncDisposable).
        /// Neither line compiles until AvaloniaFileDialogService exists with the
        /// agreed shape.
        /// </summary>
        [Fact]
        public void Service_ImplementsContract_AndAcceptsStorageProviderInjection()
        {
            var proxy = CreateProviderProxy();

            IFileDialogService service = new AvaloniaFileDialogService((IStorageProvider)proxy);

            Assert.IsAssignableFrom<IFileDialogService>(service);
            Assert.IsAssignableFrom<IAsyncDisposable>(service);
        }
    }

    /// <summary>
    /// DispatchProxy fake for Avalonia's [NotClientImplementable]
    /// IStorageProvider. Records every invoked member and delegates the four
    /// members the adapter may use to per-test handlers.
    /// </summary>
    internal class StorageProviderProxy : DispatchProxy
    {
        public bool CanOpen { get; set; } = true;
        public bool CanSave { get; set; } = true;
        public bool CanPickFolder { get; set; } = true;
        public Func<object?[], object?>? OpenFilePicker { get; set; }
        public Func<object?[], object?>? SaveFilePicker { get; set; }
        public Func<object?[], object?>? OpenFolderPicker { get; set; }
        public Func<object?[], object?>? TryGetFolderFromPath { get; set; }

        /// <summary>CLR member names invoked so far, in order.</summary>
        public List<string> Calls { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new InvalidOperationException("Proxy invoked without a target method.");
            }

            Calls.Add(targetMethod.Name);
            return targetMethod.Name switch
            {
                "get_CanOpen" => CanOpen,
                "get_CanSave" => CanSave,
                "get_CanPickFolder" => CanPickFolder,
                "OpenFilePickerAsync" => OpenFilePicker!(args ?? Array.Empty<object?>()),
                "SaveFilePickerAsync" => SaveFilePicker!(args ?? Array.Empty<object?>()),
                "OpenFolderPickerAsync" => OpenFolderPicker!(args ?? Array.Empty<object?>()),
                "TryGetFolderFromPathAsync" => TryGetFolderFromPath!(args ?? Array.Empty<object?>()),
                _ => throw new NotSupportedException(
                    $"Unexpected IStorageProvider member invoked: {targetMethod.Name}."),
            };
        }
    }

    /// <summary>
    /// DispatchProxy fake for Avalonia's [NotClientImplementable]
    /// IStorageFile/IStorageFolder. Only the Path member (and Dispose) are
    /// meaningful to the adapter; anything else invoked fails the test loudly.
    /// </summary>
    internal class StorageItemProxy : DispatchProxy
    {
        public Uri? Path { get; set; }

        /// <summary>CLR member names invoked so far, in order.</summary>
        public List<string> Calls { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new InvalidOperationException("Proxy invoked without a target method.");
            }

            Calls.Add(targetMethod.Name);
            return targetMethod.Name switch
            {
                "get_Path" => Path,
                "Dispose" => null,
                _ => throw new NotSupportedException(
                    $"Unexpected IStorageItem member invoked: {targetMethod.Name}."),
            };
        }
    }
}
