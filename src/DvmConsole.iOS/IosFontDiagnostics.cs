// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using SkiaSharp;
using Avalonia.Media;
using UIKit;

namespace DvmConsole.iOS;

/// <summary>Detects silent fallback from UIKit font identifiers in the native renderer.</summary>
internal static class IosFontDiagnostics
{
    public static string Run()
    {
        using var font = UIFont.SystemFontOfSize(17)
            ?? throw new InvalidOperationException("UIKit did not return a system font.");
        var lines = new List<string> { "PASS" };
        var resolved = new List<IGlyphTypeface>();
        foreach (int weight in new[] { 400, 600, 700 })
        {
            using var face = SKTypeface.FromFamilyName(font.FamilyName,
                new SKFontStyle(weight, 5, SKFontStyleSlant.Upright));
            if (face.FamilyName != font.FamilyName || face.FontWeight != weight)
                throw new InvalidOperationException($"System font resolved to {face.FamilyName}/{face.FontWeight}; expected {font.FamilyName}/{weight}.");
            var typeface = new Typeface(IosSystemFontCollection.Family, FontStyle.Normal, (FontWeight)weight);
            var glyph = typeface.GlyphTypeface;
            lines.Add($"Native {face.FamilyName}: weight {face.FontWeight}; Avalonia {glyph.FamilyName}: weight {(int)glyph.Weight}; simulations={glyph.FontSimulations}");
            if (resolved.Any(previous => ReferenceEquals(previous, glyph)))
                throw new InvalidOperationException("Different system weights reused one native face.");
            if (!ReferenceEquals(glyph, typeface.GlyphTypeface))
                throw new InvalidOperationException("System font resolution did not retain its cache.");
            resolved.Add(glyph);
        }
        return string.Join("\n", lines);
    }
}
