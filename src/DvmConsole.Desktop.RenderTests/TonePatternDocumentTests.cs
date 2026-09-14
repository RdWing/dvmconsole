// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class TonePatternDocumentTests
{
    [Fact]
    public async Task RoundTripPreservesShortHoldsAndContainsOnlyPatterns()
    {
        var document = new TonePatternDocument
        {
            TonePresets = [new TonePresetSetting
        {
            Name = "Test pattern", Steps = [new() { FrequencyHz = 1000, DurationSeconds = 0.3 },
                new() { Kind = "hold", DurationSeconds = 0.02 }, new() { FrequencyHz = 1500, DurationSeconds = 0.4 }]
        }]
        };
        using var stream = new MemoryStream();
        await document.WriteAsync(stream);
        stream.Position = 0;
        var read = await TonePatternDocument.ReadAsync(stream);
        Assert.Equal(0.02, read.TonePresets[0].Steps[1].DurationSeconds);
        Assert.Equal("hold", read.TonePresets[0].Steps[1].Kind);
        Assert.Equal(1500, read.TonePresets[0].Steps[2].FrequencyHz);
    }

    [Fact]
    public async Task RoundTripPreservesSavedPatternLongerThanThirtySeconds()
    {
        // Same duration and step count as the reported preset, using synthetic tones.
        var steps = Enumerable.Range(0, 24).SelectMany(_ => new[]
        {
            new TonePresetStepSetting { FrequencyHz = 1000, DurationSeconds = 1.3 },
            new TonePresetStepSetting { Kind = "hold", DurationSeconds = 0.04 }
        }).ToList();
        var document = new TonePatternDocument
        {
            TonePresets = [new TonePresetSetting { Name = "Long saved pattern", Steps = steps }]
        };
        using var stream = new MemoryStream();
        await document.WriteAsync(stream);
        stream.Position = 0;
        var restored = await TonePatternDocument.ReadAsync(stream);
        var restoredSteps = restored.TonePresets.Single().Steps;
        Assert.Equal(48, restoredSteps.Count);
        Assert.Equal(32.16, restoredSteps.Sum(step => step.DurationSeconds), 8);
        Assert.Equal(steps.Select(step => (step.Kind, step.FrequencyHz, step.DurationSeconds)),
            restoredSteps.Select(step => (step.Kind, step.FrequencyHz, step.DurationSeconds)));
    }

    [Theory]
    [InlineData("tone", 0, 1000)]
    [InlineData("tone", 11, 1000)]
    [InlineData("tone", 1, 299)]
    [InlineData("invalid", 1, 1000)]
    [InlineData("hold", 1, 1000)]
    public void RejectsMalformedPatterns(string kind, double duration, double frequency)
    {
        var document = new TonePatternDocument
        {
            TonePresets = [new TonePresetSetting
            { Steps = [new() { Kind = kind, DurationSeconds = duration, FrequencyHz = frequency }] }]
        };
        Assert.Throws<InvalidDataException>(document.Validate);
    }

    [Fact]
    public async Task RejectsOversizedInputBeforeParsing()
    {
        using var stream = new MemoryStream(new byte[1_048_577]);
        await Assert.ThrowsAsync<InvalidDataException>(() => TonePatternDocument.ReadAsync(stream));
    }
}
