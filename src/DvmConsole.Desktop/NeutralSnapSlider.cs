using Avalonia;
using Avalonia.Controls;
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

        double threshold = (maximum - minimum) * Math.Clamp(snapFraction, 0, 0.5);
        return Math.Abs(value - neutral) <= threshold ? neutral : value;
    }
}

// Adds a neutral detent while the operator clicks or drags the slider.
public sealed class NeutralSnapSlider : Slider
{
    public static readonly StyledProperty<double> NeutralValueProperty =
        AvaloniaProperty.Register<NeutralSnapSlider, double>(nameof(NeutralValue));
    public static readonly StyledProperty<double> SnapFractionProperty =
        AvaloniaProperty.Register<NeutralSnapSlider, double>(nameof(SnapFraction), 0.05);

    private bool isPointerInteraction;
    private bool isApplyingSnap;

    // Custom controls use their own theme key by default. Reuse Slider's Fluent
    // template so the track and thumb remain visible while adding only behavior.
    protected override Type StyleKeyOverride => typeof(Slider);

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
        isPointerInteraction = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        base.OnPointerPressed(e);
        SnapPointerValue();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!isPointerInteraction || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            isPointerInteraction = false;
            return;
        }

        SnapPointerValue();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        SnapPointerValue();
        isPointerInteraction = false;
    }

    protected override void OnThumbDragStarted(VectorEventArgs e)
    {
        isPointerInteraction = true;
        base.OnThumbDragStarted(e);
        SnapPointerValue();
    }

    protected override void OnThumbDragCompleted(VectorEventArgs e)
    {
        base.OnThumbDragCompleted(e);
        SnapPointerValue();
        isPointerInteraction = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && isPointerInteraction && !isApplyingSnap)
            SnapPointerValue();
    }

    private void SnapPointerValue()
    {
        double snapped = NeutralSliderMath.SnapToNeutral(
            Value,
            Minimum,
            Maximum,
            NeutralValue,
            SnapFraction);
        if (Math.Abs(snapped - Value) < double.Epsilon)
            return;

        isApplyingSnap = true;
        try
        {
            // Keep the existing two-way binding intact while moving the thumb
            // to the detent during pointer and thumb-drag interactions.
            SetCurrentValue(ValueProperty, snapped);
        }
        finally
        {
            isApplyingSnap = false;
        }
    }
}
