// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.iOS;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AVKit;
using CoreGraphics;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using UIKit;

namespace DvmConsole.iOS;

/// <summary>Qualifies native route-picker attachment and navigation without opening audio endpoints.</summary>
internal static class IosAudioRouteDiagnostics
{
    public static Task<string> RunAsync()
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { completion.TrySetResult(await RunCoreAsync()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task;
    }

    private static async Task<string> RunCoreAsync()
    {
        var lifetime = (ISingleViewApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
        var shell = (Border)lifetime.MainView!;
        Control? previous = shell.Child;
        var picker = new IosAudioRoutePicker();
        var form = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
            ? ConsoleHostFormFactor.Tablet : ConsoleHostFormFactor.Phone;
        await using var console = new MobileConsoleView(form);
        var settings = new MobileSettingsView(new TextBox(), form,
            () => console, audioRoutePicker: picker);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.Loaded += (_, _) => loaded.TrySetResult();
        try
        {
            shell.Child = settings;
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var top = TopLevel.GetTopLevel(settings)!;
            var root = ((UIViewControlHandle)top.TryGetPlatformHandle()!).View;
            for (int cycle = 0; cycle < 3; cycle++)
            {
                Click("Audio  ›");
                await RenderAsync(top);
                AVRoutePickerView native = root.Subviews.OfType<AVRoutePickerView>()
                    .Single(view => !view.Hidden && view.Window is not null);
                if (native.Bounds.Width < 44 || native.Bounds.Height < 44 || native.PrioritizesVideoDevices)
                    throw new InvalidOperationException("The native audio route target is clipped or configured for video.");
                Click("‹ Settings");
                Click("Configuration Library  ›");
                await RenderAsync(top);
                if (root.Subviews.OfType<AVRoutePickerView>().Any(view => !view.Hidden && view.Window is not null))
                    throw new InvalidOperationException("The native route picker remained over the configuration page.");
                Click("‹ Settings");
            }
            await RenderAsync(top);
            Click("Audio  ›");
            picker.BringIntoView();
            await RenderAsync(top);
            var active = root.Subviews.OfType<AVRoutePickerView>()
                .Single(view => !view.Hidden && view.Window is not null);
            string interaction = await InspectInteractionAsync(active);
            return "PASS\nNative audio route picker: 48-point target, three Settings/Library attachment cycles, no overlay left on the configuration page. " +
                interaction + " No microphone request or physical-device qualification.";
        }
        finally { shell.Child = previous; }

        void Click(string label) => settings.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task<string> InspectInteractionAsync(AVRoutePickerView picker)
    {
        var window = picker.Window!;
        CGPoint center = picker.ConvertPointToView(new CGPoint(picker.Bounds.Width / 2, picker.Bounds.Height / 2), window);
        UIView? hit = window.HitTest(center, null);
        if (hit is null || (!ReferenceEquals(hit, picker) && !hit.IsDescendantOfView(picker)))
            throw new InvalidOperationException($"The route picker center hit {hit?.GetType().Name ?? "nothing"} instead of its native control.");

        int seconds = int.TryParse(Environment.GetEnvironmentVariable("DVM_SESSION_INSPECT_SECONDS"), out int duration)
            ? Math.Clamp(duration, 0, 300) : 0;
        if (seconds == 0) return "Native center hit testing reaches the picker; route presentation was not exercised.";

        var controls = Descendants(picker).OfType<UIControl>().ToArray();
        int touchDown = 0;
        int touchUp = 0;
        EventHandler down = (_, _) => touchDown++;
        EventHandler up = (_, _) => touchUp++;
        using var presentation = new RoutePresentationObserver();
        var previousDelegate = picker.Delegate;
        picker.Delegate = presentation;
        foreach (var control in controls)
        {
            control.TouchDown += down;
            control.TouchUpInside += up;
        }
        try
        {
            await File.WriteAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "session-inspect-ready.txt"),
                $"Route picker native hit test passed. Tap its AirPlay icon during the next {seconds} seconds; public touch and presentation callbacks are observed.");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            return $"Observed native touches down={touchDown}, upInside={touchUp}; route presentation began={presentation.Began}, ended={presentation.Ended}. " +
                (presentation.Began > 0 ? "Native presentation began; output selection still requires route qualification."
                    : "No native route presentation observed; route UI remains unqualified.");
        }
        finally
        {
            foreach (var control in controls)
            {
                control.TouchDown -= down;
                control.TouchUpInside -= up;
            }
            picker.Delegate = previousDelegate;
        }
    }

    private static IEnumerable<UIView> Descendants(UIView parent)
    {
        foreach (UIView child in parent.Subviews)
        {
            yield return child;
            foreach (UIView descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class RoutePresentationObserver : AVRoutePickerViewDelegate
    {
        public int Began { get; private set; }
        public int Ended { get; private set; }
        public override void WillBeginPresentingRoutes(AVRoutePickerView routePickerView) => Began++;
        public override void DidEndPresentingRoutes(AVRoutePickerView routePickerView) => Ended++;
    }

    private static async Task RenderAsync(TopLevel top)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        for (int frame = 0; frame < 3; frame++)
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            top.RequestAnimationFrame(_ => rendered.TrySetResult());
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
