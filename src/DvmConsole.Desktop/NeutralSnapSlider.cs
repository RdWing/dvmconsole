using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace DvmConsole.Desktop;

public static class NeutralSliderMath
{
    public static double VolumeGainToPosition(double gain)
    {
        double normalized = double.IsFinite(gain) ? Math.Clamp(gain, 0, 4) : 1;
        return normalized <= 1 ? normalized - 1 : (normalized - 1) / 3;
    }

    public static double VolumePositionToGain(double position)
    {
        double normalized = double.IsFinite(position) ? Math.Clamp(position, -1, 1) : 0;
        return normalized <= 0 ? normalized + 1 : 1 + (normalized * 3);
    }

    public static double SnapToNeutral(
        double value,
        double minimum,
        double maximum,
        double neutral,
        double snapFraction)
    {
        if (!double.IsFinite(value) || !double.IsFinite(minimum) || !double.IsFinite(maximum) ||
            !double.IsFinite(neutral) || maximum <= minimum)
        {
            return value;
        }

        double threshold = (maximum - minimum) * Math.Clamp(snapFraction, 0, 0.5) / 2;
        return Math.Abs(value - neutral) <= threshold ? neutral : value;
    }
}

// Adds a neutral detent only while the operator is actively pointer-dragging the thumb.
public sealed class NeutralSnapSlider : Slider
{
    public static readonly StyledProperty<double> NeutralValueProperty =
        AvaloniaProperty.Register<NeutralSnapSlider, double>(nameof(NeutralValue));
    public static readonly StyledProperty<double> SnapFractionProperty =
        AvaloniaProperty.Register<NeutralSnapSlider, double>(nameof(SnapFraction), 0.05);

    private bool isThumbDrag;

    public double NeutralValue
    {
        get => GetValue(NeutralValueProperty);
        set => SetValue(NeutralValueProperty, value);
    }

    public double SnapFraction
    {
        get => GetValue(SnapFractionProperty);
        set => SetValue(SnapFractionProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        isThumbDrag = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && HasThumbAncestor(e.Source);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!isThumbDrag || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            isThumbDrag = false;
            return;
        }

        Value = NeutralSliderMath.SnapToNeutral(Value, Minimum, Maximum, NeutralValue, SnapFraction);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        isThumbDrag = false;
    }

    private static bool HasThumbAncestor(object? source)
    {
        for (StyledElement? element = source as StyledElement;
             element is not null;
             element = element.Parent as StyledElement)
        {
            if (element is Thumb)
                return true;
        }

        return false;
    }
}
