// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using DvmConsole.Core.Settings;

namespace DvmConsole.Presentation;

/// <summary>Portable, preset-only exchange format. No settings, paths or credentials are exported.</summary>
public sealed class TonePatternDocument
{
    public int Version { get; set; } = 1;
    public List<TonePresetSetting> TonePresets { get; set; } = [];
    private const int MaximumBytes = 1_048_576;

    public static async Task<TonePatternDocument> ReadAsync(Stream input)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("Tone pattern file exceeds 1 MiB.");
            buffer.Write(chunk, 0, count);
        }
        var document = JsonSerializer.Deserialize(buffer.ToArray(), TonePatternJsonContext.Default.TonePatternDocument)
            ?? throw new InvalidDataException("The tone pattern file is empty.");
        document.Validate();
        return document;
    }

    public Task WriteAsync(Stream output)
    {
        Validate();
        return JsonSerializer.SerializeAsync(output, this, TonePatternJsonContext.Default.TonePatternDocument);
    }

    public void Validate()
    {
        if (Version != 1 || TonePresets is null || TonePresets.Count is < 1 or > 256)
            throw new InvalidDataException("Expected version 1 with 1–256 tone patterns.");
        foreach (var preset in TonePresets)
        {
            if (preset is null || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 160 || preset.Steps is null)
                throw new InvalidDataException("Every tone pattern needs a name and valid steps.");
            var steps = new TonePresetViewModel(preset).Steps;
            if (steps.Count is < 1 or > 512) throw new InvalidDataException("A pattern must contain 1–512 steps.");
            bool hasTone = false;
            foreach (var step in steps)
            {
                if (step is null || !double.IsFinite(step.DurationSeconds) || step.DurationSeconds <= 0 || step.DurationSeconds > 10)
                    throw new InvalidDataException($"Invalid duration in '{preset.Name}'.");
                bool hold = string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase);
                if (!hold && (!string.Equals(step.Kind, AudioPresetStepKinds.Tone, StringComparison.OrdinalIgnoreCase) ||
                    !double.IsFinite(step.FrequencyHz) || step.FrequencyHz < 300 || step.FrequencyHz > 2500))
                    throw new InvalidDataException($"Invalid tone step in '{preset.Name}'.");
                hasTone |= !hold;
            }
            if (!hasTone) throw new InvalidDataException($"'{preset.Name}' must contain at least one tone step.");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TonePatternDocument))]
internal sealed partial class TonePatternJsonContext : JsonSerializerContext;
