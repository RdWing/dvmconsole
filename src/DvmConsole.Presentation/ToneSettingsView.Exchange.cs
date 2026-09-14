// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class ToneSettingsView
{
    public Func<TonePatternDocument, Task>? ImportPatternsAsync { get; set; }
    public Func<TonePatternDocument?>? ExportPatterns { get; set; }
    private bool exchanging;
    public Func<Task<TonePatternDocument?>>? PickImportDocumentAsync { get; set; }
    public Func<TonePatternDocument, Task>? SaveExportDocumentAsync { get; set; }

    private async void HandleImportPatternsClick(object? sender, RoutedEventArgs args)
        => await ExchangeAsync(async () =>
        {
            var import = ImportPatternsAsync;
            var pick = PickImportDocumentAsync;
            if (import is null || pick is null) return;
            var document = await pick();
            if (document is not null && ReferenceEquals(import, ImportPatternsAsync))
                await import(document);
        });

    private async void HandleExportPatternsClick(object? sender, RoutedEventArgs args)
        => await ExchangeAsync(async () =>
        {
            var document = ExportPatterns?.Invoke();
            var save = SaveExportDocumentAsync;
            if (document is null || save is null) return;
            document.Validate();
            await save(document);
        });

    private async Task ExchangeAsync(Func<Task> action)
    {
        if (exchanging) return;
        var status = this.FindControl<TextBlock>("PatternExchangeStatus")!;
        exchanging = true;
        status.Text = "";
        try
        {
            await action();
        }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { exchanging = false; }
    }
}
