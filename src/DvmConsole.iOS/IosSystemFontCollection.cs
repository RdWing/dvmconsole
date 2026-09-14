// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace DvmConsole.iOS;

/// <summary>
/// Keeps Apple's variable system font instances keyed by the requested style.
/// Avalonia's system collection otherwise substitutes styles using the font's
/// generic OS/2 weight (400), which does not describe the selected native instance.
/// </summary>
internal sealed class IosSystemFontCollection(string nativeFamily) : FontCollectionBase
{
    public static readonly FontFamily Family = new("fonts:NeoSystem#System");
    private readonly object gate = new();
    private IFontManagerImpl? platform;
    public override Uri Key { get; } = new("fonts:NeoSystem");
    public override int Count => 1;
    public override FontFamily this[int index] => index == 0 ? Family : throw new ArgumentOutOfRangeException(nameof(index));
    public override void Initialize(IFontManagerImpl fontManager) => platform = fontManager;
    public override IEnumerator<FontFamily> GetEnumerator() { yield return Family; }

    public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight,
        FontStretch stretch, [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        if (familyName != "System" || platform is null) return false;
        var key = new FontCollectionKey { Style = style, Weight = weight, Stretch = stretch };
        lock (gate)
        {
            var faces = _glyphTypefaceCache.GetOrAdd("System", _ => new ConcurrentDictionary<FontCollectionKey, IGlyphTypeface?>());
            if (faces.TryGetValue(key, out glyphTypeface)) return glyphTypeface is not null;
            if (!platform.TryCreateGlyphTypeface(nativeFamily, style, weight, stretch, out glyphTypeface)) return false;
            faces[key] = glyphTypeface;
            return true;
        }
    }

    public override bool TryMatchCharacter(int codepoint, FontStyle style, FontWeight weight,
        FontStretch stretch, string? familyName, CultureInfo? culture, out Typeface match)
    {
        match = default;
        if (!TryGetGlyphTypeface("System", style, weight, stretch, out var glyph) ||
            !glyph.TryGetGlyph((uint)codepoint, out _)) return false;
        match = new Typeface(Family, style, weight, stretch);
        return true;
    }
}
