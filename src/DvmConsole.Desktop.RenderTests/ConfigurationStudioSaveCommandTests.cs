// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class ConfigurationStudioSaveCommandTests
{
    [AvaloniaTheory]
    [InlineData("Save configuration copy")]
    [InlineData("Review and save configuration")]
    public async Task FooterCommandCompletionIncludesTheSaveMessage(string automationName)
    {
        using DemoSessionState state = DemoSessionState.Create();
        var main = new MainWindow(
            Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml"),
            new UserSettingsStore(state.UserSettingsPath),
            new OperatorViewStore(state.OperatorViewPath),
            demoMode: true);
        ConfigurationStudioWindow? studio = null;
        var messageReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dismissMessage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? completion = null;

        try
        {
            main.Show();
            studio = main.CreateConfigurationStudioForCapture(ConfigurationStudioSection.Systems);
            studio.DialogConfirmationOverride = (title, _, _) => Task.FromResult(title == "Review & Save");
            studio.MessageOverride = (title, _) =>
            {
                if (title != "Configuration saved")
                    throw new InvalidOperationException($"Unexpected save result: {title}");
                messageReached.TrySetResult();
                return dismissMessage.Task;
            };
            studio.Show(main);
            studio.StudioViewModel.Systems[0].Name += " save command";
            studio.StudioViewModel.CommitFieldEdit();
            studio.UpdateLayout();
            Assert.True(studio.StudioViewModel.IsDirty);
            Assert.Null(studio.SaveCommandCompletionForCapture);

            Button button = Assert.Single(studio.GetVisualDescendants().OfType<Button>(),
                button => button.IsEffectivelyVisible && AutomationProperties.GetName(button) == automationName);
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            completion = Assert.IsAssignableFrom<Task>(studio.SaveCommandCompletionForCapture);
            await messageReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(studio.StudioViewModel.IsDirty);
            Assert.False(completion.IsCompleted);
            dismissMessage.SetResult();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(completion.IsCompletedSuccessfully);
        }
        finally
        {
            dismissMessage.TrySetResult();
            try
            {
                if (completion is not null)
                    await completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                studio?.CloseForSessionReplacement();
                main.Close();
            }
        }
    }
}
