// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

internal sealed class MobileRecordingSettingsView : UserControl
{
    public event EventHandler? SettingsRequested;
    private readonly Func<MobileSession> session;
    private readonly IConsoleApplicationSession owner;
    private readonly IConsoleRecordingSettings? settings;
    private readonly NumericUpDown days = new() { Minimum = 0, Maximum = 3650, Increment = 1, FormatString = "0", MinHeight = 48 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button review = new() { Content = "Review retention", MinHeight = 48 };
    private readonly Button apply = new() { Content = "Apply reviewed retention", MinHeight = 48, IsEnabled = false };
    private int? reviewedDays;
    private bool busy;

    public MobileRecordingSettingsView(Func<MobileSession> session)
    {
        this.session = session;
        owner = session().Application;
        settings = owner.Commands as IConsoleRecordingSettings;
        var back = new Button { Content = "‹ Settings", MinHeight = 48 };
        back.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "Recording retention (days)", TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(days, "Recording retention days");
        days.Value = settings?.RecordingRetention.Days ?? 7;
        body.Children.Add(days);
        body.Children.Add(new TextBlock { Text = "0 keeps recordings indefinitely. Review shows how many completed recordings are eligible for deletion. Apply enables cleanup now and when opening a configuration. This setting covers all recordings on this device.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(review);
        body.Children.Add(status);
        body.Children.Add(apply);
        days.ValueChanged += (_, _) => { reviewedDays = null; apply.IsEnabled = false; status.Text = "Review the new retention period before applying it."; };
        review.Click += async (_, _) => await ReviewAsync();
        apply.Click += async (_, _) => await ApplyAsync();
        bool available = settings?.CanSaveRecordingRetention == true;
        days.IsEnabled = review.IsEnabled = available;
        status.Text = !available ? "Open a configuration first." : settings!.RecordingRetention.Accepted && settings.RecordingRetention.Days > 0
            ? $"Automatic deletion is enabled after {settings.RecordingRetention.Days} days."
            : "Automatic deletion is disabled.";
        Content = MobileSettingsPageLayout.Create(MobileSettingsPageLayout.Heading("Retention", back), body);
    }

    private bool IsCurrent()
    {
        if (ReferenceEquals(owner, session().Application)) return true;
        status.Text = "The active configuration changed. Reopen Recordings from Settings.";
        reviewedDays = null;
        apply.IsEnabled = false;
        return false;
    }

    private void SetBusy(bool value)
    {
        busy = value;
        days.IsEnabled = review.IsEnabled = !value && settings?.CanSaveRecordingRetention == true;
        apply.IsEnabled = !value && reviewedDays.HasValue;
    }

    private async Task ReviewAsync()
    {
        if (busy || settings?.CanSaveRecordingRetention != true || !IsCurrent()) return;
        if (days.Value is not { } value || value != decimal.Truncate(value))
        { status.Text = "Enter a whole number of days from 0 to 3650."; return; }
        reviewedDays = null;
        SetBusy(true);
        try
        {
            var preview = await settings.PreviewRecordingRetentionAsync((int)value);
            if (!IsCurrent()) return;
            reviewedDays = (int)value;
            status.Text = preview.Cutoff is { } cutoff
                ? $"{preview.CandidateCount:N0} recording(s) ended before {cutoff.LocalDateTime:g} are eligible for deletion. Apply will remove expired recordings; the count may change as recordings finish."
                : "All recordings will be kept. Automatic deletion will be disabled.";
        }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { SetBusy(false); }
    }

    private async Task ApplyAsync()
    {
        if (busy || settings is null || !IsCurrent() || reviewedDays is not { } value) return;
        SetBusy(true);
        try
        {
            await settings.SetRecordingRetentionAsync(new(value, Accepted: true));
            if (IsCurrent()) status.Text = value == 0 ? "Saved. Automatic deletion is disabled." : $"Saved. Recordings are kept for {value} days.";
            reviewedDays = null;
        }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { SetBusy(false); }
    }
}
