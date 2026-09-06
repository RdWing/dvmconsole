// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DvmConsole.Presentation;

public readonly record struct AudioMeterState(double Level, double PeakLevel);

// A render-only meter: level updates invalidate pixels without rebuilding or
// remeasuring a tree of clipped borders and canvases.
public sealed class AudioLevelMeter : Control
{
    public static readonly StyledProperty<AudioMeterState> MeterProperty =
        AvaloniaProperty.Register<AudioLevelMeter, AudioMeterState>(nameof(Meter));
    public static readonly StyledProperty<bool> ColorizePeakProperty =
        AvaloniaProperty.Register<AudioLevelMeter, bool>(nameof(ColorizePeak), true);

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#24303B"));
    private static readonly SolidColorBrush NormalPeakBrush = new(Color.Parse("#F5F7FA"));
    private static readonly SolidColorBrush YellowPeakBrush = new(Color.Parse("#F2B134"));
    private static readonly SolidColorBrush RedPeakBrush = new(Color.Parse("#E5484D"));
    private static readonly LinearGradientBrush LevelBrush = new()
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.Parse("#00BE5A"), 0),
            new GradientStop(Color.Parse("#00BE5A"), 0.76),
            new GradientStop(Color.Parse("#F2B134"), 0.76),
            new GradientStop(Color.Parse("#F2B134"), 0.88),
            new GradientStop(Color.Parse("#E5484D"), 0.88),
            new GradientStop(Color.Parse("#E5484D"), 1)
        ]
    };

    static AudioLevelMeter()
        => AffectsRender<AudioLevelMeter>(MeterProperty, ColorizePeakProperty);

    public AudioMeterState Meter
    {
        get => GetValue(MeterProperty);
        set => SetValue(MeterProperty, value);
    }

    public bool ColorizePeak
    {
        get => GetValue(ColorizePeakProperty);
        set => SetValue(ColorizePeakProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        double radius = Math.Min(height / 2, 3);
        var bounds = new Rect(0, 0, width, height);
        context.DrawRectangle(TrackBrush, null, bounds, radius, radius);

        double level = Normalize(Meter.Level);
        double fillWidth = width * level / 100;
        if (fillWidth > 0)
        {
            using (context.PushClip(new Rect(0, 0, fillWidth, height)))
                context.DrawRectangle(LevelBrush, null, bounds, radius, radius);
        }

        double peak = Normalize(Meter.PeakLevel);
        if (peak <= 0)
            return;
        double peakX = Math.Clamp(width * peak / 100 - 1, 0, Math.Max(0, width - 2));
        IBrush peakBrush = GetPeakColor(peak, ColorizePeak) switch
        {
            var color when color == RedPeakBrush.Color => RedPeakBrush,
            var color when color == YellowPeakBrush.Color => YellowPeakBrush,
            _ => NormalPeakBrush
        };
        context.DrawRectangle(peakBrush, null, new Rect(peakX, 0, 2, height));
    }

    internal static Color GetPeakColor(double peak, bool colorize)
        => !colorize
            ? NormalPeakBrush.Color
            : peak >= 88
                ? RedPeakBrush.Color
                : peak >= 76
                    ? YellowPeakBrush.Color
                    : NormalPeakBrush.Color;

    private static double Normalize(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
}
