// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Media;

namespace DvmConsole.Mobile;

/// <summary>The approved console glyphs share a 24-point canvas and rounded 1.8-point strokes.</summary>
internal static class MobileConsoleIcons
{
    private static readonly Geometry Bell = Geometry.Parse(
        "M18 8 A6 6 0 0 0 6 8 C6 15 3 15 3 17 H21 C21 15 18 15 18 8 M10 21 H14");
    private static readonly Geometry Sound = Geometry.Parse(
        "M4 9 H8 L13 5 V19 L8 15 H4 Z M17 8 A6 6 0 0 1 17 16 M20 5 A10 10 0 0 1 20 19");
    private static readonly Geometry Muted = Geometry.Parse(
        "M4 9 H8 L13 5 V19 L8 15 H4 Z M17 9 L23 15 M23 9 L17 15");
    private static readonly Geometry Gear = Geometry.Parse(
        "M9 3 L8 6 L5 7 V10 L3 12 L5 14 V17 L8 18 L9 21 H15 L16 18 L19 17 V14 L21 12 L19 10 V7 L16 6 L15 3 Z M15 12 A3 3 0 1 0 9 12 A3 3 0 1 0 15 12");

    public static Control Alert() => Create(Bell);
    public static Control Settings() => Create(Gear);
    public static Control Speaker(bool muted) => Create(muted ? Muted : Sound);

    private static Control Create(Geometry data)
    {
        // PathIcon inherits the button foreground through its template. A resource
        // binding on a Path nested inside a Canvas can remain unresolved when the
        // console replaces the startup view on iOS.
        var stroke = new Pen(Brushes.White, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        return new PathIcon { Width = 22, Height = 22, Data = data.GetWidenedGeometry(stroke) };
    }
}
