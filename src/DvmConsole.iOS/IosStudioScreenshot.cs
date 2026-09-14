// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using DvmConsole.Storage;
using UIKit;

namespace DvmConsole.iOS;

/// <summary>Temporarily presents an isolated Studio fixture for an explicit simulator screenshot.</summary>
internal static class IosStudioScreenshot
{
    public static async Task CaptureAsync(MobileStudioSession session, ConfigurationId id, string root)
    {
        if (Environment.GetEnvironmentVariable("DVM_STUDIO_SCREENSHOT") != "1") return;
        string requested = Environment.GetEnvironmentVariable("DVM_STUDIO_SECTION") ?? "Overview";
        if (!Enum.TryParse<ConfigurationStudioSection>(requested, true, out var section) || !Enum.IsDefined(section))
            throw new ArgumentException("Unknown Studio screenshot section.");
        session.ViewModel.SelectSection(section);
        var lifetime = (ISingleViewApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
        var shell = (Border)lifetime.MainView!;
        Control? previous = shell.Child;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new MobileConfigurationStudioView(session, id, _ => Task.FromResult(false),
            () => Task.CompletedTask, () => new ConfigurationExportArchive(Path.Combine(root, "Screenshots")));
        editor.Loaded += (_, _) => loaded.TrySetResult();
        var form = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
            ? ConsoleHostFormFactor.Tablet : ConsoleHostFormFactor.Phone;
        var settings = new MobileSettingsView(editor, form, () => throw new InvalidOperationException("Screenshot has no radio session."));
        var overviewLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.Loaded += (_, _) => overviewLoaded.TrySetResult();
        try
        {
            shell.Child = settings;
            await overviewLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            settings.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Configuration Library  ›"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            TopLevel topLevel = TopLevel.GetTopLevel(editor)!;
            for (int frame = 0; frame < 3; frame++)
            {
                var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                topLevel.RequestAnimationFrame(_ => rendered.TrySetResult());
                await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            await File.WriteAllTextAsync(Path.Combine(documents, "studio-ui-ready.txt"), "Studio fixture ready");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(Path.Combine(documents, "studio-ui-captured.txt")))
                await Task.Delay(100, deadline.Token);
        }
        finally { shell.Child = previous; }
    }
}
