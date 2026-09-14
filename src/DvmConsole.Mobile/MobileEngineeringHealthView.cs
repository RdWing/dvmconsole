// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Mobile;

/// <summary>On-demand engineering telemetry with no refresh work while hidden.</summary>
internal sealed class MobileEngineeringHealthView : UserControl
{
    public event EventHandler? SettingsRequested;
    private readonly Func<MobileSession> session;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock status = Text();
    private readonly TextBlock receive = Text();
    private readonly TextBlock latency = Text();
    private readonly TextBlock microphone = Text();
    private readonly TextBlock transmit = Text();
    private readonly TextBlock finalization = Text();
    private readonly TextBlock catalog = Text();
    private readonly TextBlock recovery = Text();
    private CancellationTokenSource? lifetime;
    private IConsoleApplicationSession? displayedSession;
    private bool refreshing;

    public MobileEngineeringHealthView(Func<MobileSession> session)
    {
        this.session = session;
        var body = new StackPanel { Spacing = 16 };
        var back = new Button { Content = "‹ Settings", MinHeight = 44 };
        AutomationProperties.SetName(back, "Back to Settings");
        back.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        foreach (var line in new[] { status, receive, latency, microphone, transmit, finalization, catalog, recovery })
            body.Children.Add(line);
        Content = MobileSettingsPageLayout.Create(MobileSettingsPageLayout.Heading("Engineering health", back), body);
        timer.Tick += async (_, _) => await RefreshAsync();
        AttachedToVisualTree += async (_, _) =>
        {
            lifetime = new();
            timer.Start();
            await RefreshAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
        };
    }

    private async Task RefreshAsync()
    {
        if (refreshing || lifetime is not { } owner) return;
        var current = session().Application;
        if (!ReferenceEquals(current, displayedSession))
        {
            displayedSession = current;
            receive.Text = latency.Text = microphone.Text = transmit.Text =
                finalization.Text = catalog.Text = recovery.Text = string.Empty;
        }
        if (current.Commands is not IConsoleEngineeringHealth health)
        {
            status.Text = "Open a configuration to view engineering health.";
            return;
        }
        refreshing = true;
        try
        {
            var snapshot = await health.CaptureHealthAsync(owner.Token);
            if (!ReferenceEquals(owner, lifetime) || !ReferenceEquals(current, session().Application)) return;
            status.Text = $"Updated {snapshot.CapturedAt.ToLocalTime():HH:mm:ss}";
            receive.Text = OperationalHealthPresentation.FormatReceiveQueue(snapshot.ReceiveQueue);
            latency.Text = OperationalHealthPresentation.FormatLatency(snapshot.ReceiveLatency);
            microphone.Text = OperationalHealthPresentation.FormatMicrophoneEngineering(snapshot.Microphone);
            transmit.Text = OperationalHealthPresentation.FormatWorkBacklog("TX work", snapshot.Transmit);
            finalization.Text = OperationalHealthPresentation.FormatWorkBacklog("TAR finalization", snapshot.RecordingFinalization);
            catalog.Text = OperationalHealthPresentation.FormatCatalog(snapshot.RecordingCatalog);
            recovery.Text = OperationalHealthPresentation.FormatRouteRecovery(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(owner, lifetime)) status.Text = $"Health unavailable: {exception.Message}";
        }
        finally { refreshing = false; }
    }

    private static TextBlock Text() => new() { TextWrapping = TextWrapping.Wrap };
}
