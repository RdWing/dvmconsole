// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DvmConsole.Audio;

using DvmConsole.Mobile;

namespace DvmConsole.iOS;

/// <summary>Explicit, permission-gated input selection through the application's audio owner.</summary>
internal sealed record IosInputSelectionContext(IosAudioSessionOwner Audio, Action<string?> Save);

internal sealed class IosAudioInputSettings : UserControl
{
    private readonly Func<IosInputSelectionContext> getContext;
    private readonly Button refresh = new() { Content = "Choose microphone", MinHeight = 48 };
    private readonly ComboBox inputs = new() { MinHeight = 48, HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };
    private readonly Button apply = new() { Content = "Use selected microphone", MinHeight = 48, IsVisible = false };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap,
        Text = "Microphone access is requested only when needed. Choosing a microphone pauses audio; resume listening afterward." };
    private CancellationTokenSource? pending;
    private IosInputSelectionContext? selectionContext;

    public IosAudioInputSettings(Func<IosInputSelectionContext> getContext)
    {
        this.getContext = getContext;
        AutomationProperties.SetName(inputs, "Microphone input");
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = "Microphone", FontWeight = FontWeight.SemiBold }.WithScaledFontSize(18));
        body.Children.Add(status);
        body.Children.Add(refresh);
        body.Children.Add(inputs);
        body.Children.Add(apply);
        Content = body;
        refresh.Click += async (_, _) => await RefreshAsync();
        apply.Click += (_, _) => Apply();
        DetachedFromVisualTree += (_, _) => pending?.Cancel();
    }

    private async Task RefreshAsync()
    {
        if (pending is not null) return;
        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        refresh.IsEnabled = false;
        inputs.IsVisible = apply.IsVisible = false;
        selectionContext = null;
        try
        {
            var context = getContext();
            var owner = context.Audio;
            IReadOnlyList<AudioDeviceInfo> available = await owner.PrepareInputSelectionAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var choices = new List<InputChoice> { new(null, "System default") };
            choices.AddRange(available.Select(input => new InputChoice(input.Id, input.Name)));
            string? preferred = owner.PreferredInputId;
            int selected = choices.FindIndex(choice => choice.Id == preferred);
            if (selected < 0)
            {
                choices.Add(new(preferred, "Saved microphone (unavailable)"));
                selected = choices.Count - 1;
            }
            inputs.ItemsSource = choices;
            inputs.SelectedIndex = selected;
            selectionContext = context;
            inputs.IsVisible = apply.IsVisible = true;
            status.Text = available.Count == 0
                ? "No microphone routes are available. Resume listening when ready."
                : "Select a microphone, then resume listening. Transmission remains stopped.";
        }
        catch (OperationCanceledException) { status.Text = "Microphone selection cancelled. Resume listening if audio is paused."; }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { pending = null; refresh.IsEnabled = true; }
    }

    private void Apply()
    {
        if (inputs.SelectedItem is not InputChoice selected || selectionContext is null) return;
        try
        {
            selectionContext.Save(selected.Id);
            status.Text = $"{selected.Name} saved for this configuration. Resume listening when ready.";
            inputs.IsVisible = apply.IsVisible = false;
        }
        catch (Exception exception) { status.Text = exception.Message; }
    }

    private sealed record InputChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }
}
