// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using DvmConsole.Storage;
using Foundation;
using Markdown.Avalonia;
using System.Text.Json;
using UIKit;

namespace DvmConsole.iOS;

internal static class IosHelpDiagnostics
{
    public static async Task<string> RunAsync()
    {
        string root = Path.Combine(NSBundle.MainBundle.BundlePath, "Documentation");
        IConsoleHelpCatalog catalog = DocumentationCatalog.Open(root);
        var topics = await catalog.FindAsync().ConfigureAwait(false);
        if (topics.Count == 0) throw new InvalidOperationException("The bundled guide contains no topics.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "manifest.json")));
        int images = 0;
        foreach (var asset in manifest.RootElement.GetProperty("assets").EnumerateArray())
        {
            using var image = new Bitmap(Path.Combine(root, asset.GetString()!));
            if (image.PixelSize.Width <= 0 || image.PixelSize.Height <= 0)
                throw new InvalidOperationException("A bundled help image did not decode.");
            images++;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RenderTopicsAsync(catalog, topics); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        await completion.Task.ConfigureAwait(false);
        return $"PASS\nBundled shared guide: {topics.Count} topics rendered through Settings/Help, {images} packaged images decoded, same-topic and cross-topic heading links scrolled. No radio, audio or external network access.";
    }

    private static async Task RenderTopicsAsync(IConsoleHelpCatalog catalog, IReadOnlyList<ConsoleHelpTopic> topics)
    {
        var lifetime = (ISingleViewApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
        var shell = (Border)lifetime.MainView!;
        Control? previous = shell.Child;
        var form = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
            ? ConsoleHostFormFactor.Tablet : ConsoleHostFormFactor.Phone;
        await using var console = new MobileConsoleView(form);
        var settings = new MobileSettingsView(new TextBox(), form,
            () => console, createHelpCatalog: () => catalog);
        try
        {
            shell.Child = settings;
            var top = TopLevel.GetTopLevel(settings)!;
            await FrameAsync(top);
            settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Help  ›"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(top, () => settings.GetVisualDescendants().OfType<ListBox>().SingleOrDefault()?.Items.OfType<MobileHelpView.HelpRow>().Count(row => row.IsTopic) == topics.Count);
            var list = settings.GetVisualDescendants().OfType<ListBox>().Single();
            var reader = settings.GetVisualDescendants().OfType<MarkdownScrollViewer>().Single();
            foreach (var topic in topics)
            {
                string expected = await catalog.ReadAsync(topic.Id);
                list.SelectedItem = list.Items.OfType<MobileHelpView.HelpRow>().Single(item => item.Topic?.Id == topic.Id);
                await WaitAsync(top, () => reader.Markdown == expected);
                await FrameAsync(top);
                if (reader.Bounds.Width < 100 || reader.Bounds.Height < 100 ||
                    !reader.GetVisualDescendants().Any(control => control is TextBlock ||
                        control.GetType().FullName == "ColorTextBlock.Avalonia.CTextBlock"))
                    throw new InvalidOperationException($"Help content did not render: {topic.Title}");
            }
            ConsoleHelpTopic configuration = topics.Single(topic => topic.Id.EndsWith("01-Codeplug Creation.md", StringComparison.Ordinal));
            string configurationText = await catalog.ReadAsync(configuration.Id);
            list.SelectedItem = list.Items.OfType<MobileHelpView.HelpRow>().Single(item => item.Topic?.Id == configuration.Id);
            await WaitAsync(top, () => reader.Markdown == configurationText);
            var engine = (Markdown.Avalonia.Markdown)reader.Engine;
            var links = engine.HyperlinkCommand ?? throw new InvalidOperationException("Help navigation is not attached.");
            links.Execute("#encryption-keys-and-rid-aliases");
            await WaitAsync(top, () => reader.ScrollValue.Y > 100);
            string groupText = await catalog.ReadAsync(topics.Single(topic => topic.Id.EndsWith("04-Groups and Patching.md", StringComparison.Ordinal)).Id);
            links.Execute("04-Groups%20and%20Patching.md#patch-groups");
            await WaitAsync(top, () => reader.Markdown == groupText && reader.ScrollValue.Y > 100);
        }
        finally { shell.Child = previous; }
    }

    private static async Task WaitAsync(TopLevel top, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await FrameAsync(top).WaitAsync(timeout.Token);
    }

    private static async Task FrameAsync(TopLevel top)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        top.RequestAnimationFrame(_ => frame.TrySetResult());
        await frame.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
