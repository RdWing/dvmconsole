// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Mobile;
using DvmConsole.Storage;
using System.IO.Compression;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileConfigurationTests
{
    [AvaloniaFact]
    public async Task NewConfigurationCreatesAnUnsavedDraftWithoutImportingOrStartingRadio()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mobile-new-{Guid.NewGuid():N}");
        Window? window = null;
        try
        {
            var library = new ManagedConfigurationLibrary(root);
            var created = new TaskCompletionSource<ConfigurationDraft>(TaskCreationOptions.RunContinuationsAsynchronously);
            var view = new MobileConfigurationView(() => library,
                createExportArchive: () => new ConfigurationExportArchive(Path.Combine(root, "Exports")),
                createDraftStudio: (_, draft, _) =>
                {
                    created.SetResult(draft);
                    return ValueTask.FromException<MobileStudioSession>(new OperationCanceledException());
                });
            window = new Window { Width = 390, Height = 844, Content = view };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await view.Initialization;
            window.UpdateLayout();
            Button create = view.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "New configuration"));
            create.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            ConfigurationDraft opened = await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(opened.BasedOnRevision);
            Assert.True(opened.IsDirty);
            Assert.Empty(DvmConsole.Core.Configuration.ConfigurationDocument.Parse(opened.Yaml).Configuration.Systems);
            Assert.Null(library.Active);
            var configurations = new List<ConfigurationSummary>();
            await foreach (var item in library.ListAsync()) configurations.Add(item);
            Assert.Empty(configurations);
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task StartupReopensPinnedActiveRevisionAndPropagatesFailures()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mobile-startup-{Guid.NewGuid():N}");
        try
        {
            var library = new ManagedConfigurationLibrary(root);
            ConfigurationDraft draft = await library.CreateDraftAsync("Startup");
            draft = draft with { Yaml = """
                systems:
                  - name: Simulator
                    identity: Console
                    address: 127.0.0.1
                    port: 62031
                    peerId: 1
                    rid: "1001"
                zones: []
                groups: []
                """ };
            ConfigurationCommit first = await library.CommitAsync(draft);
            await library.ActivateAsync(first.Reference);
            ConfigurationDraft edit = await library.OpenDraftAsync(first.Reference.Id);
            ConfigurationCommit second = await library.CommitAsync(edit with { Yaml = edit.Yaml + "\n# Later save\n" });
            Assert.NotEqual(first.Reference, second.Reference);
            var reopened = new ManagedConfigurationLibrary(root);
            ConfigurationReference? opened = null;
            bool fail = false;
            var view = new MobileConfigurationView(() => reopened, openConsole: (_, reference, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (fail) throw new IOException("Unavailable storage");
                opened = reference;
                return Task.CompletedTask;
            });
            Assert.True(await view.OpenActiveAsync());
            Assert.Equal(first.Reference, opened);
            Assert.Equal(first.Reference, reopened.Active);
            fail = true;
            await Assert.ThrowsAsync<IOException>(() => view.OpenActiveAsync());
            Assert.Equal(first.Reference, reopened.Active);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.OpenActiveAsync(cancelled.Token));
            await reopened.DeactivateAsync();
            Assert.False(await view.OpenActiveAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task MobileLibraryReopensThePersistedRevisionWithoutStartingRadioServices()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mobile-library-{Guid.NewGuid():N}");
        Window? window = null;
        try
        {
            var library = new ManagedConfigurationLibrary(root);
            ConfigurationDraft draft = await library.CreateDraftAsync("Simulator configuration");
            draft = draft with { Yaml = """
                systems:
                  - name: Simulator
                    identity: Console
                    address: 127.0.0.1
                    port: 62031
                    peerId: 1
                    rid: "1001"
                zones: []
                groups: []
                """ };
            ConfigurationCommit commit = await library.CommitAsync(draft);
            var reopened = new ManagedConfigurationLibrary(root);
            var view = new MobileConfigurationView(reopened);
            window = new Window { Width = 390, Height = 844, Content = view };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await view.Initialization;
            window.UpdateLayout();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Simulator configuration");
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Inspect YAML"));
            Assert.Null(reopened.Active);
            var entries = new List<ConfigurationSummary>();
            await foreach (ConfigurationSummary entry in reopened.ListAsync()) entries.Add(entry);
            Assert.Equal(commit.Reference.Revision, Assert.Single(entries).CurrentRevision);
        }
        finally
        {
            window?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BufferedExportRemainsReadableAfterTheLibraryDisposesItsWriter()
    {
        using var destination = new BufferedYamlExport();
        await using (Stream output = await destination.OpenWriteAsync())
        {
            await output.WriteAsync("systems: []\n"u8.ToArray());
        }
        await using Stream input = await destination.OpenReadAsync();
        using var reader = new StreamReader(input);
        Assert.Equal("systems: []\n", await reader.ReadToEndAsync());
    }
    [Fact]
    public async Task FullArchiveRoundTripsManagedCompanionsIntoAnotherLibrary()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mobile-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string yamlPath = Path.Combine(root, "source.yaml");
            await File.WriteAllTextAsync(yamlPath, """
                systems:
                  - name: Test
                    identity: Console
                    address: 127.0.0.1
                    port: 62031
                    peerId: 1
                    rid: "1001"
                    aliasPath: ./aliases.yml
                zones: []
                groups: []
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "aliases.yml"), "[]\n");
            var source = new ManagedConfigurationLibrary(Path.Combine(root, "library"));
            ConfigurationImportResult imported = await source.ImportAsync(new FileConfigurationDocumentSet(yamlPath), new());
            await using var export = new ConfigurationExportArchive(Path.Combine(root, "exports"));
            await source.ExportAsync(imported.Reference, export, new ConfigurationExportOptions(false, true));
            IReadableDocument result = await export.CreateArchiveAsync();
            string extracted = Path.Combine(root, "extracted");
            await using (Stream zip = await result.OpenReadAsync())
                ZipFile.ExtractToDirectory(zip, extracted);
            Assert.Equal("[]\n", await File.ReadAllTextAsync(Path.Combine(extracted, "aliases.yml")));
            var destination = new ManagedConfigurationLibrary(Path.Combine(root, "destination"));
            ConfigurationImportResult restored = await destination.ImportAsync(
                new FileConfigurationDocumentSet(Path.Combine(extracted, "configuration.yaml")), new());
            Assert.Empty(restored.Warnings);
            using var yaml = new BufferedYamlExport();
            await destination.ExportAsync(restored.Reference, yaml, new ConfigurationExportOptions(false, false));
            await using Stream contents = await yaml.OpenReadAsync();
            using var reader = new StreamReader(contents);
            Assert.Contains("./aliases.yml", await reader.ReadToEndAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

}
