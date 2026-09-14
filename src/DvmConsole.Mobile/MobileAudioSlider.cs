// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace DvmConsole.Mobile;

internal static class MobileAudioSlider
{
    public static Control Field(string label, Slider slider, Func<double, double>? displayValue = null, Func<double, double>? sliderValue = null,
        string format = "0.##", bool snapEnteredValue = false)
    {
        // A drag beginning inside the slider belongs to it, not the page scroller.
        slider.AddHandler(InputElement.PointerPressedEvent, (_, args) => args.PreventGestureRecognition(), RoutingStrategies.Tunnel);
        var value = new TextBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 44,
            Width = 84,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        Avalonia.Automation.AutomationProperties.SetName(value, label + " exact value");
        string rendered = string.Empty;
        void Commit()
        {
            // A rounded display must not overwrite the underlying quarter-dB value
            // merely because the operator focused and left the field.
            if (value.Text == rendered) return;
            if (double.TryParse(value.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out double entered) && double.IsFinite(entered))
            {
                double next = sliderValue?.Invoke(entered) ?? entered;
                if (snapEnteredValue && slider.TickFrequency > 0)
                    next = slider.Minimum + Math.Round((next - slider.Minimum) / slider.TickFrequency,
                        MidpointRounding.AwayFromZero) * slider.TickFrequency;
                slider.Value = Math.Clamp(next, slider.Minimum, slider.Maximum);
            }
            Refresh();
        }
        value.LostFocus += (_, _) => Commit();
        value.KeyDown += (_, args) => { if (args.Key == Avalonia.Input.Key.Enter) { Commit(); args.Handled = true; } };
        void Refresh() => value.Text = rendered = (displayValue?.Invoke(slider.Value) ?? slider.Value).ToString(format, System.Globalization.CultureInfo.CurrentCulture);
        slider.PropertyChanged += (_, change) => { if (change.Property == Slider.ValueProperty) Refresh(); };
        Refresh();
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        heading.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(value, 1);
        heading.Children.Add(value);
        var field = new StackPanel { Spacing = 2 };
        field.Children.Add(heading);
        field.Children.Add(slider);
        return field;
    }
}
