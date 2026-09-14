// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DvmConsole.Presentation;

namespace DvmConsole.Host;

// Compiled into each host so document handles stay outside shared presentation.
internal sealed class TonePatternFilePicker(Control owner)
{
    private static readonly FilePickerFileType PatternFile = new("Console tone patterns")
    {
        Patterns = ["*.json"],
        AppleUniformTypeIdentifiers = ["public.json"]
    };

    private IStorageProvider Storage => TopLevel.GetTopLevel(owner)?.StorageProvider
        ?? throw new InvalidOperationException("Document selection is unavailable.");

    public async Task<TonePatternDocument?> ImportAsync()
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import tone patterns",
            AllowMultiple = false,
            FileTypeFilter = [PatternFile]
        });
        try
        {
            if (files.Count == 0) return null;
            await using var input = await files[0].OpenReadAsync();
            return await TonePatternDocument.ReadAsync(input);
        }
        finally
        {
            foreach (var file in files) file.Dispose();
        }
    }

    public async Task ExportAsync(TonePatternDocument document)
    {
        document.Validate();
        using var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export tone patterns",
            SuggestedFileName = "console-tone-patterns.json",
            DefaultExtension = "json",
            FileTypeChoices = [PatternFile]
        });
        if (file is null) return;
        await using var output = await file.OpenWriteAsync();
        if (output.CanSeek) output.SetLength(0);
        await document.WriteAsync(output);
        await output.FlushAsync();
    }
}
