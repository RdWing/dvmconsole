// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class ChannelCardContent : UserControl
{
    public static readonly StyledProperty<double> UiFontSizeProperty =
        AvaloniaProperty.Register<ChannelCardContent, double>(nameof(UiFontSize), 12);
    public static readonly StyledProperty<double> UiSmallFontSizeProperty =
        AvaloniaProperty.Register<ChannelCardContent, double>(nameof(UiSmallFontSize), 11);

    public ChannelCardContent()
    {
        InitializeComponent();
    }

    private bool useTouchLayout;

    /// <summary>Fits the card to its content while reserving a 44-point slider target.</summary>
    public bool UseTouchLayout
    {
        get => useTouchLayout;
        set
        {
            if (useTouchLayout == value) return;
            useTouchLayout = value;
            Classes.Set("touch", value);
            var encryption = this.FindControl<Button>("EncryptionButton")!;
            var heading = this.FindControl<Grid>("CardHeading")!;
            var volume = this.FindControl<Grid>("VolumeRow")!;
            (encryption.Parent as Panel)?.Children.Remove(encryption);
            (value ? heading : volume).Children.Add(encryption);
            encryption.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            encryption.MinHeight = value ? 44 : 0;

            this.FindControl<Button>("PttButton")!.Height = value ? double.NaN : 24;
            this.FindControl<Button>("PttButton")!.MinHeight = value ? 44 : 0;
            this.FindControl<Grid>("CardLayout")!.RowDefinitions[3].Height = value ? GridLength.Auto : GridLength.Star;
            this.FindControl<Grid>("CardLayout")!.RowDefinitions[4].Height = value ? GridLength.Auto : new GridLength(24);
            var meter = this.FindControl<AudioLevelMeter>("CardMeter")!;
            if (value)
            {
                meter.Width = double.NaN;
                meter.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            }
            else
            {
                meter.Width = DataContext is IChannelCardViewModel model ? model.AudioMeterWidth : double.NaN;
                meter.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            }
            this.FindControl<NeutralSnapSlider>("VolumeSlider")!.Classes.Set("touch-slider", value);
        }
    }

    public event EventHandler<RoutedEventArgs>? TransmitSelectionClick;
    public event EventHandler<RoutedEventArgs>? PageSelectionClick;
    public event EventHandler<RoutedEventArgs>? AlertSelectionClick;

    public double UiFontSize
    {
        get => GetValue(UiFontSizeProperty);
        set => SetValue(UiFontSizeProperty, value);
    }

    public double UiSmallFontSize
    {
        get => GetValue(UiSmallFontSizeProperty);
        set => SetValue(UiSmallFontSizeProperty, value);
    }

    private void HandleTransmitSelectionClick(object? sender, RoutedEventArgs e)
        => TransmitSelectionClick?.Invoke(sender, e);

    private void HandlePageSelectionClick(object? sender, RoutedEventArgs e)
        => PageSelectionClick?.Invoke(sender, e);

    private void HandleAlertSelectionClick(object? sender, RoutedEventArgs e)
        => AlertSelectionClick?.Invoke(sender, e);

    private void InitializeComponent()
        => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
