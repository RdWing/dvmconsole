// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DvmConsole.Storage;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class ConfigurationLibraryWindow : Window
{
    private readonly IConfigurationLibrary library;
    private readonly ConfigurationLibraryViewModel viewModel = new();

    public ConfigurationLibraryWindow()
    {
        library = null!;
        InitializeComponent();
        DataContext = viewModel;
    }

    internal ConfigurationLibraryWindow(IConfigurationLibrary library)
    {
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        InitializeComponent();
        DataContext = viewModel;
        Opened += HandleOpened;
    }

    public event Func<ConfigurationLibraryItemViewModel, Task<bool>>? ActivateRequested;
    internal ConfigurationLibraryViewModel ViewModel => viewModel;

    internal async Task RefreshAsync()
    {
        if (viewModel.IsBusy)
            return;
        viewModel.IsBusy = true;
        try
        {
            ConfigurationSummary[] configurations = await ReadAllAsync(library.ListAsync());
            ConfigurationSummary[] trash = await ReadAllAsync(library.ListTrashAsync());
            viewModel.Replace(configurations, trash);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
            await ShowMessageAsync("Configuration Library unavailable", exception.Message);
        }
        finally
        {
            viewModel.IsBusy = false;
        }
    }

    private async void HandleOpened(object? sender, EventArgs e)
        => await RefreshAsync();

    private async void HandleRefreshRequested(object? sender, EventArgs e)
        => await RefreshAsync();

    private async void HandleActivateRequested(object? sender, ConfigurationLibraryItemEventArgs e)
    {
        if (viewModel.IsBusy || ActivateRequested is not { } activate)
            return;
        viewModel.IsBusy = true;
        try
        {
            if (await activate(e.Item))
                await RefreshCoreAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
                KeyNotFoundException or ObjectDisposedException)
        {
            await ShowMessageAsync("Configuration could not be opened", exception.Message);
        }
        finally
        {
            viewModel.IsBusy = false;
        }
    }

    private async void HandleTrashRequested(object? sender, ConfigurationLibraryItemEventArgs e)
    {
        if (viewModel.IsBusy || !e.Item.CanMoveToTrash)
            return;
        if (!await ConfirmAsync(
                "Move configuration to trash?",
                $"Move '{e.Item.Name}' to the recoverable Configuration Library trash? Imported YAML files are not changed.",
                "Move to Trash"))
        {
            return;
        }

        viewModel.IsBusy = true;
        try
        {
            await library.MoveToTrashAsync(e.Item.Summary.Id);
            await RefreshCoreAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                KeyNotFoundException or ObjectDisposedException)
        {
            await ShowMessageAsync("Configuration could not be removed", exception.Message);
        }
        finally
        {
            viewModel.IsBusy = false;
        }
    }

    private async void HandleRestoreRequested(object? sender, ConfigurationLibraryItemEventArgs e)
    {
        if (viewModel.IsBusy)
            return;
        viewModel.IsBusy = true;
        try
        {
            await library.RestoreFromTrashAsync(e.Item.Summary.Id);
            await RefreshCoreAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                KeyNotFoundException or ObjectDisposedException)
        {
            await ShowMessageAsync("Configuration could not be restored", exception.Message);
        }
        finally
        {
            viewModel.IsBusy = false;
        }
    }

    private async void HandleExportBundleRequested(object? sender, ConfigurationLibraryItemEventArgs e)
    {
        if (viewModel.IsBusy) return;
        viewModel.IsBusy = true;
        try
        {
            if (!await ConfirmAsync("Export configuration bundle",
                "The ZIP contains this saved revision, aliases and companion files, including passwords and encryption keys. Store it only in a location you trust.",
                "Export full bundle")) return;
            using IStorageFile? destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export configuration bundle",
                SuggestedFileName = $"configuration-{e.Item.Summary.Id}.zip",
                DefaultExtension = "zip",
                FileTypeChoices = [new FilePickerFileType("Configuration bundle") { Patterns = ["*.zip"] }]
            });
            if (destination is null) return;
            await using var archive = new ConfigurationExportArchive(Path.Combine(Path.GetTempPath(), "dvmconsole-exports"));
            await library.ExportAsync(new ConfigurationReference(e.Item.Summary.Id, e.Item.Summary.CurrentRevision),
                archive, new ConfigurationExportOptions(Sanitized: false, IncludeCompanions: true));
            IReadableDocument document = await archive.CreateArchiveAsync();
            await using Stream input = await document.OpenReadAsync();
            await using Stream output = await destination.OpenWriteAsync();
            if (output.CanSeek) output.SetLength(0);
            await input.CopyToAsync(output);
            await output.FlushAsync();
            await ShowMessageAsync("Bundle exported", "On iPhone or iPad, use Configuration Library → Import from Files and select the ZIP.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await ShowMessageAsync("Bundle could not be exported", exception.Message);
        }
        finally { viewModel.IsBusy = false; }
    }

    private async Task RefreshCoreAsync()
    {
        ConfigurationSummary[] configurations = await ReadAllAsync(library.ListAsync());
        ConfigurationSummary[] trash = await ReadAllAsync(library.ListTrashAsync());
        viewModel.Replace(configurations, trash);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        bool confirmed = false;
        OperatorDialogParts parts = OperatorDialogFactory.CreateConfirmation(title, message, confirmLabel);
        parts.CancelButton!.Click += (_, _) => parts.Window.Close();
        parts.PrimaryButton.Click += (_, _) =>
        {
            confirmed = true;
            parts.Window.Close();
        };
        await parts.Window.ShowDialog(this);
        return confirmed;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        OperatorDialogParts parts = OperatorDialogFactory.CreateMessage(title, message, "OK");
        parts.PrimaryButton.Click += (_, _) => parts.Window.Close();
        await parts.Window.ShowDialog(this);
    }

    private static async Task<T[]> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var values = new List<T>();
        await foreach (T value in source)
            values.Add(value);
        return values.ToArray();
    }

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
